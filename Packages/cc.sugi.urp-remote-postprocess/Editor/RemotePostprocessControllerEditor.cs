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
