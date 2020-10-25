using System.IO;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Lantern.Face.Parts.Html;
using Lantern.Face.Json;

namespace Lantern.Face.Parts {
	public class DelegateButton : ButtonElement {

		// experimental. probably not thread-safe and frankly i don't know how to fix it
		// also a potential ddos vector
		public static bool UnderstoodWarning = false; 
		public DelegateButton(){
			if(!UnderstoodWarning) Console.WriteLine("Face.Parts.DelegateButton is experimental and unstable and should not be used in its current form. Set DelegateButton.UnderstoodWarning = true to suppress this message.");
			Attribs["type"] = "button";
		}

		// delegates are passed the http context that notified us of the button click
		public delegate Task Delegate(HttpContext context, Dictionary<string, JsValue> formData);
		// todo: wrap HttpContext to allow use with other HTTP suites

		internal class CacheEntry {
			public DelegateButton Button;
			public UInt64 TouchedTime;
			public bool Expired => Button.ExpireTimestamp != null && GetCurrentTimestamp() > Button.ExpireTimestamp;
		}

		private static ulong GetCurrentTimestamp(){
			return Convert.ToUInt64(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds());
		}

		public static void Touch(string[] ids){
			foreach(var id in ids) if (cache.ContainsKey(id)) {
				cache[id].TouchedTime = GetCurrentTimestamp();
			}
		}

		private static Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>();
		public readonly ButtonElement ButtonElement;

		public bool Once {
			get => Data.Keys.Contains("once");
			set {
				if (value) {
					Data["once"] = "1";
				} else if (Data.Keys.Contains("once")) {
					Data.Remove("once");
				}
			}
		}


		public ulong? ExpireTimestamp;

		public ulong? TimeToLiveSeconds {
			set {
				if (value == null) { ExpireTimestamp = null; }
				else { ExpireTimestamp = GetCurrentTimestamp() + value; }
				Data["expires"] = ExpireTimestamp.ToString();
			}
			get {
				if(ExpireTimestamp == null) return null;
				return ExpireTimestamp - GetCurrentTimestamp();
			}
		}

		private Delegate _delegate;
		public Delegate OnClick {
			get => _delegate;
			set {
				string id = CreateID();
				ulong time = GetCurrentTimestamp();
				if (cache.Count > autoCleanupMinimumCacheSize && time > lastCacheClearTime + autoCleanupSecondsBetween) RemoveExpired();
				if(value == _delegate) return;
				if (_delegate != null) {
					// a delegate was already assigned and should now be inaccessible; remove it from the cache
					var existingPair = cache.FirstOrDefault(pair => pair.Value.Button == this);
					cache.Remove(existingPair.Key);
				}
				_delegate = value;
				cache[id] = new CacheEntry() {
					TouchedTime = time,
					Button = this,
				};
				Data["face-script"] = "Face.Parts.DelegateButton";
				// this clientscript will prime the button element to fetch('/.face/delegate/{id}') which will in turn call DelegateButton.Fire(id)
				Data["delegateId"] = id;
			}
		}

		// Generate a unique reference string for a delegate. authenticity checking not necessary; knowing the ID is as knowing an auth token
		private string CreateID(){
			using (RandomNumberGenerator rng = new RNGCryptoServiceProvider()) {
				byte[] tokenData = new byte[36];
				rng.GetBytes(tokenData);
				return Convert.ToBase64String(tokenData).Replace('/', '.');
			}
		}

		// This lets smaller services run cleanup far less frequently at the expense of a little RAM
		private const int autoCleanupMinimumCacheSize = 512;

		// DDoS prevention; even non-modifying cleanup is potentially expensive so restrict public triggering
		private const long autoCleanupSecondsBetween = 20;
		private static ulong lastCacheClearTime = 0;

		// Time in seconds to remove untouched cache entries
		private const int pingTimeout = 300;

		
		public static void RemoveExpired(){
			var time = GetCurrentTimestamp();
			lastCacheClearTime = time;
			var numBefore = cache.Count();
			foreach(var k in cache.Keys){
				var entry = cache[k];
				if(entry.Expired || time > entry.TouchedTime + pingTimeout) cache.Remove(k);
			}
			var numAfter = cache.Count();
			Console.WriteLine("Cache purge: " + (numBefore - numAfter).ToString() + " removed");
		}

		// Locates and calls the identified delegate against the given HTTP context
		public static async Task Fire(string guid, HttpContext context){
			ulong time = GetCurrentTimestamp();
			if (cache.Count > autoCleanupMinimumCacheSize && time > lastCacheClearTime + autoCleanupSecondsBetween) RemoveExpired();

			if(!cache.Keys.Contains(guid)){
				context.Response.ContentType = "text/plain";
				await context.Response.WriteAsync("unavailable");
				return;
			}

			CacheEntry d = cache[guid];

			if(d.Expired){
				cache.Remove(guid);
				await context.Response.WriteAsync("expired");
				return;
			}

			d.TouchedTime = time;
			CacheEntry entry = cache[guid];
			if(d.Button.Once) cache.Remove(guid);
			string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
			try {
				Dictionary<string, JsValue> formData = body.Length == 0
					? new Dictionary<string, JsValue>()
					: JsValue.FromJson(body).ObjectValue;
				await entry.Button._delegate(context, formData);
			} catch (ParseError) { } // ignore malformed body
		}

		// include DelegateButton's own requirement plus those of its content
		public override string[] GetClientRequires() {
			List<string> list = new List<string>(){
				"Face.Parts.General"
			};
			foreach(var part in Content) list.AddRange(part.GetClientRequires());
			return list.ToArray();
		}
	}
}