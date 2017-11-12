﻿// Copyright (c) Microsoft. ALl rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace MonoDevelop.MSBuildEditor.Schema
{
	class MSBuildSchema
	{
		public Dictionary<string, PropertyInfo> Properties { get; } = new Dictionary<string, PropertyInfo> (StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, ItemInfo> Items { get; } = new Dictionary<string, ItemInfo> (StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, TaskInfo> Tasks { get; } = new Dictionary<string, TaskInfo> (StringComparer.OrdinalIgnoreCase);

		public void Load (TextReader reader)
		{
			JObject doc;
			using (var jr = new JsonTextReader (reader)) {
				doc = JObject.Load (jr);
			}

			foreach (var kv in doc) {
				switch(kv.Key) {
				case "properties":
					LoadProperties ((JObject)kv.Value);
					break;
				case "items":
					LoadItems ((JObject)kv.Value);
					break;
				case "license":
					break;
				default:
					throw new Exception ($"Unknown property {kv.Key} in root");
				}
			}
		}

		void LoadProperties (JObject properties)
		{
			foreach (var kv in properties) {
				var name = kv.Key;
				string description = null, valueSeparator = null, defaultValue = null;
				List<ValueInfo> values = null;
				foreach (var pkv in (JObject)kv.Value) {
					switch (pkv.Key) {
					case "description":
						description = (string)pkv.Value;
						break;
					case "values":
						values = GetValues ((JObject)pkv.Value);
						break;
					case "defaultValue":
						defaultValue = (string)((JValue)pkv.Value).Value;
						break;
					case "valueSeparator":
						valueSeparator = (string)((JValue)pkv.Value).Value;
						break;
					default:
						throw new Exception ($"Unknown property {pkv.Key} in property {kv.Key}");
					}
				}
				Properties [name] = new PropertyInfo (name, description, false, false, values, defaultValue, valueSeparator);
			}
		}

		void LoadItems (JObject items)
		{
			foreach (var kv in items) {
				var name = kv.Key;
				string description = null;
				bool isFile = false;
				Dictionary<string, MetadataInfo> metadata = null;
				foreach (var ikv in (JObject)kv.Value) {
					switch (ikv.Key) {
					case "description":
						description = (string)((JValue)ikv.Value).Value;
						break;
					case "isFile":
						isFile = (bool)((JValue)ikv.Value).Value;
						break;
					case "metadata":
						metadata = GetMetadata ((JObject)ikv.Value);
						break;
					default:
						throw new Exception ($"Unknown property {ikv.Key} in item {kv.Key}");
					}
				}
				Items [name] = new ItemInfo (name, description, isFile, metadata);
			}
		}

		Dictionary<string, MetadataInfo> GetMetadata (JObject metaObj)
		{
			var metadata = new Dictionary<string, MetadataInfo> ();
			foreach (var kv in metaObj) {
				var name = kv.Key;
				string description = null, valueSeparator = null, defaultValue = null;
				bool required = false;
				List<ValueInfo> values = null;
				foreach (var mkv in (JObject)kv.Value) {
					switch (mkv.Key) {
					case "description":
						description = (string)((JValue)mkv.Value).Value;
						break;
					case "values":
						switch(mkv.Value) {
						case JValue jv:
							var metaRef = (string)jv.Value;
							if (!metaRef.StartsWith ("%(", StringComparison.Ordinal) || metaRef [metaRef.Length - 1] != ')')
								throw new Exception ($"Metadata reference '{metaRef} on {mkv.Key} has invalid format'");
							metaRef = metaRef.Substring (2, metaRef.Length - 3);
							values = metadata [metaRef].Values;
							break;
						case JObject jo:
								values = GetValues (jo);
							break;
						}
						break;
					case "valueSeparator":
						valueSeparator = (string)((JValue)mkv.Value).Value;
						break;
					case "defaultValue":
						defaultValue = (string)((JValue)mkv.Value).Value;
						break;
					case "required":
						required = (bool)((JValue)mkv.Value).Value;
						break;
					default:
						throw new Exception ($"Unknown property {mkv.Key} in metadata {kv.Key}");
					}
				}
				metadata [name] = new MetadataInfo (name, description, false, required, values, defaultValue, valueSeparator);
			}
			return metadata;
		}

		List<ValueInfo> GetValues (JObject value)
		{
			var values = new List<ValueInfo> ();
			foreach (var ikv in value) {
				values.Add (new ValueInfo (ikv.Key, (string)((JValue)ikv.Value).Value));
			}
			return values;
		}
	}
}