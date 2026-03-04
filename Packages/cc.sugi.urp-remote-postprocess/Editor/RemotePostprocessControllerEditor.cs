using System.IO;
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
            DrawDefaultInspector();

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

            var controller = (RemotePostprocessController)target;
            if (GUILayout.Button("Apply Selected Preset To Current Profile"))
            {
                controller.ApplySelectedPresetToCurrentProfileInEditMode();
            }
        }
    }
}
