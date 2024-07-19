using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JsonDiffPatchDotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using RogueEssence.Dev;
using RogueEssence.LevelGen;

namespace RogueEssence.Data
{
    [Serializable]
    public class SerializationContainer
    {
        public Version Version;
        public object Object;
    }

    public static class Serializer
    {
        public static JsonSerializerSettings Settings { get; private set; }
        private static JsonDiffPatch jdp;

        public static void InitSettings(IContractResolver resolver, ISerializationBinder binder)
        {
            Settings = new JsonSerializerSettings()
            {
                ContractResolver = resolver,
                SerializationBinder = binder,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                TypeNameHandling = TypeNameHandling.Auto,
            };
            jdp = new JsonDiffPatch();
        }

        /// <summary>
        /// A value that is temporarily set when deserializing a data object, serving as a global old version for converters in UpgradeConverters.cs to recognize the version.
        /// A bit hacky, but is currently the only way for converters to recognize version.
        /// </summary>
        public static Version OldVersion;

        private static object lockObj = new object();

        public static object Deserialize(Stream stream, Type type, params JsonConverter[] converters)
        {
            lock (lockObj)
            {
                object obj;
                Version pastVersion = OldVersion;
                OldVersion = Versioning.GetVersion();
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, -1, true))
                {
                    JsonSerializerSettings settingsCopy = new JsonSerializerSettings(Settings);
                    settingsCopy.Converters = converters;
                    obj = JsonConvert.DeserializeObject(reader.ReadToEnd(), type, settingsCopy);
                }
                OldVersion = pastVersion;
                return obj;
            }
        }

        public static void Serialize(Stream stream, object entry)
        {
            using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8, -1, true))
            {
                string val = JsonConvert.SerializeObject(entry, Settings);
                writer.Write(val);
            }
        }

        public static void Serialize(Stream stream, object entry, params JsonConverter[] converters)
        {
            using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8, -1, true))
            {
                JsonSerializerSettings settingsCopy = new JsonSerializerSettings(Settings);
                settingsCopy.Converters = converters;
                string val = JsonConvert.SerializeObject(entry, settingsCopy);
                writer.Write(val);
            }
        }

        public static Version GetVersion(string containerStr)
        {
            Version objVersion = new Version(0, 0);
            try
            {
                using (JsonTextReader textReader = new JsonTextReader(new StringReader(containerStr)))
                {
                    textReader.Read();
                    textReader.Read();
                    while (true)
                    {
                        if (textReader.TokenType == JsonToken.PropertyName && (string)textReader.Value == "Version")
                        {
                            textReader.Read();
                            objVersion = new Version((string)textReader.Value);
                            break;
                        }
                        else
                        {
                            textReader.Skip();
                            textReader.Read();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagManager.Instance.LogError(ex);
            }
            return objVersion;
        }
        public static object DeserializeDataWithDiffs(string path, params string[] diffpaths)
        {
            //read the base string
            JToken containerToken;
            using (Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, -1, true))
                {
                    string containerStr = reader.ReadToEnd();
                    containerToken = JToken.Parse(containerStr);
                }
            }

            //for each diff, read into an array
            JToken[] diffTokens = new JToken[diffpaths.Length];
            for (int ii = 0; ii < diffpaths.Length; ii++)
            {
                using (Stream stream = new FileStream(diffpaths[ii], FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, -1, true))
                    {
                        string containerStr = reader.ReadToEnd();
                        diffTokens[ii] = JToken.Parse(containerStr);
                    }
                }
            }

            //then, apply all diff patches (including version)
            foreach(JToken diff in diffTokens)
                containerToken = jdp.Patch(containerToken, diff);

            //then, load from this string
            {
                string containerStr = containerToken.ToString();
                Version pastVersion = OldVersion;
                OldVersion = GetVersion(containerStr);
                SerializationContainer container = (SerializationContainer)JsonConvert.DeserializeObject(containerStr, typeof(SerializationContainer), Settings);
                OldVersion = pastVersion;
                return container.Object;
            }
        }

        public static object DeserializeData(Stream stream)
        {
            lock (lockObj)
            {
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, -1, true))
                {
                    string containerStr = reader.ReadToEnd();
                    //Temporarily set global old version for converters in UpgradeConverters.cs to recognize the version.
                    Version pastVersion = OldVersion;
                    OldVersion = GetVersion(containerStr);
                    SerializationContainer container = (SerializationContainer)JsonConvert.DeserializeObject(containerStr, typeof(SerializationContainer), Settings);
                    OldVersion = pastVersion;
                    return container.Object;
                }
            }
        }

        public static void SerializeDataAsDiff(string path, string basepath, object entry)
        {
            //save the current object json to a string
            JToken currentToken;
            {
                SerializationContainer container = new SerializationContainer();
                container.Object = entry;
                container.Version = Versioning.GetVersion();
                string containerStr = SerializeObjectInternal(container, Settings, true);
                currentToken = JToken.Parse(containerStr);
            }

            //load the original object json
            JToken baseToken;
            using (Stream stream = new FileStream(basepath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, -1, true))
                {
                    string containerStr = reader.ReadToEnd();
                    baseToken = JToken.Parse(containerStr);
                }
            }

            //diff the two
            JToken diffToken = jdp.Diff(baseToken, currentToken);

            //null diff?  no diff!
            if (diffToken == null)
            {
                //no need to save anything
                //delete if exists
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            //save the resulting diff string to the path
            using (Stream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8, -1, true))
                {
                    writer.Write(diffToken.ToString());
                }
            }
        }

        public static void SerializeData(Stream stream, object entry, bool min = false)
        {
            using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8, -1, true))
            {
                SerializationContainer container = new SerializationContainer();
                container.Object = entry;
                container.Version = Versioning.GetVersion();
                string val = SerializeObjectInternal(container, Settings, min);
                writer.Write(val);
            }
        }

        private static string SerializeObjectInternal(object value, JsonSerializerSettings settings, bool min)
        {
            JsonSerializer jsonSerializer = JsonSerializer.CreateDefault(settings);
            StringBuilder sb = new StringBuilder(256);
            StringWriter sw = new StringWriter(sb, CultureInfo.InvariantCulture);
            using (JsonTextWriter jsonWriter = new JsonTextWriter(sw))
            {
                if (min)
                {
                    jsonWriter.Formatting = Formatting.None;
                }
                else
                {
                    jsonWriter.Formatting = Formatting.Indented;
                    jsonWriter.Indentation = 0;
                    jsonWriter.IndentChar = '\t';
                }

                jsonSerializer.Serialize(jsonWriter, value, null);
            }

            return sw.ToString();
        }

    }
}