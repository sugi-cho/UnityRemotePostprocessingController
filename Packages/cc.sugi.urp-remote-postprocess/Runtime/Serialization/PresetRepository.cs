using System.Collections.Generic;
using System.IO;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Serialization
{
    public sealed class PresetRepository
    {
        private const string RootFolder = "RemotePostprocess/presets";

        public bool Save(PresetData data, string presetName)
        {
            if (data == null || string.IsNullOrWhiteSpace(presetName))
            {
                return false;
            }

            string path = GetPresetPath(presetName);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            return true;
        }

        public PresetData Load(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return null;
            }

            string path = GetPresetPath(presetName);
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            return JsonUtility.FromJson<PresetData>(json);
        }

        public IReadOnlyList<string> ListPresets()
        {
            string dir = GetRootDirectory();
            if (!Directory.Exists(dir))
            {
                return new List<string>();
            }

            string[] files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            var names = new List<string>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                names.Add(Path.GetFileNameWithoutExtension(files[i]));
            }

            return names;
        }

        public bool Exists(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return false;
            }

            return File.Exists(GetPresetPath(presetName));
        }

        public bool Delete(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return false;
            }

            string path = GetPresetPath(presetName);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }

        public bool Rename(string fromPresetName, string toPresetName)
        {
            if (string.IsNullOrWhiteSpace(fromPresetName) || string.IsNullOrWhiteSpace(toPresetName))
            {
                return false;
            }

            if (string.Equals(fromPresetName, toPresetName))
            {
                return true;
            }

            string fromPath = GetPresetPath(fromPresetName);
            string toPath = GetPresetPath(toPresetName);
            if (!File.Exists(fromPath) || File.Exists(toPath))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(toPath) ?? string.Empty);
            File.Move(fromPath, toPath);
            return true;
        }

        private static string GetPresetPath(string presetName)
        {
            return Path.Combine(GetRootDirectory(), $"{presetName}.json");
        }

        private static string GetRootDirectory()
        {
            return Path.Combine(Application.persistentDataPath, RootFolder);
        }
    }
}
