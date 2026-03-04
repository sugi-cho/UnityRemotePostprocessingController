using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Bootstrap;
using UnityEditor;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Editor
{
    [CustomEditor(typeof(RemotePostprocessController))]
    public sealed class RemotePostprocessControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var controller = (RemotePostprocessController)target;
            DrawDefaultInspector();
            DrawWebUiUrlSection(controller);

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Utilities", EditorStyles.boldLabel);
            if (GUILayout.Button("Open RemotePostprocess Folder"))
            {
                string folderPath = Path.Combine(Application.persistentDataPath, "RemotePostprocess");
                Directory.CreateDirectory(folderPath);
                EditorUtility.RevealInFinder(folderPath);
            }

            if (Application.isPlaying)
            {
                return;
            }

            GUILayout.Space(8);
            EditorGUILayout.LabelField("EditMode Tools", EditorStyles.boldLabel);

            if (GUILayout.Button("Apply Selected Preset To Current Profile"))
            {
                controller.ApplySelectedPresetToCurrentProfileInEditMode();
            }
        }

        private static void DrawWebUiUrlSection(RemotePostprocessController controller)
        {
            GUILayout.Space(8);
            EditorGUILayout.LabelField("WebUI URL", EditorStyles.boldLabel);
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("接続は Play 実行中に有効です。", MessageType.None);
            }

            string localhostUrl = BuildWebUiUrl("localhost", controller.Port);
            DrawUrlRow("Localhost", localhostUrl);

            List<string> lanUrls = GetLanUrls(controller.Port);
            if (lanUrls.Count == 0)
            {
                EditorGUILayout.HelpBox("LAN 内IPが見つかりませんでした。", MessageType.Info);
                return;
            }

            for (int i = 0; i < lanUrls.Count; i++)
            {
                DrawUrlRow($"LAN {i + 1}", lanUrls[i]);
            }
        }

        private static void DrawUrlRow(string label, string url)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);
            EditorGUILayout.SelectableLabel(url, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("Copy", GUILayout.Width(50)))
            {
                EditorGUIUtility.systemCopyBuffer = url;
            }

            if (GUILayout.Button("Open", GUILayout.Width(50)))
            {
                Application.OpenURL(url);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string BuildWebUiUrl(string host, int port)
        {
            return $"http://{host}:{port}/";
        }

        private static List<string> GetLanUrls(int port)
        {
            var urls = new List<string>();
            var seen = new HashSet<string>();
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface networkInterface = interfaces[i];
                if (networkInterface == null
                    || networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties props;
                try
                {
                    props = networkInterface.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                for (int j = 0; j < props.UnicastAddresses.Count; j++)
                {
                    IPAddress address = props.UnicastAddresses[j].Address;
                    if (address == null
                        || address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address)
                        || IsApipa(address))
                    {
                        continue;
                    }

                    string host = address.ToString();
                    if (!seen.Add(host))
                    {
                        continue;
                    }

                    urls.Add(BuildWebUiUrl(host, port));
                }
            }

            urls.Sort();
            return urls;
        }

        private static bool IsApipa(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
        }
    }
}
