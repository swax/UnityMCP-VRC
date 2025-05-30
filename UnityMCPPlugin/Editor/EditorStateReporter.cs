using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace UnityMCP.Editor
{
    public class EditorStateReporter
    {
        private string lastErrorMessage = "";

        // New method to send editor state
        public async Task SendEditorState(ClientWebSocket webSocket, CancellationToken cancellationToken)
        {
            try
            {
                if (webSocket?.State == WebSocketState.Open)
                {
                    var state = GetEditorState();
                    var message = JsonConvert.SerializeObject(new
                    {
                        type = "editorState",
                        data = state
                    });
                    var buffer = Encoding.UTF8.GetBytes(message);
                    await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
                    Debug.Log("[UnityMCP] Sent editor state upon request");
                }
                else
                {
                    Debug.LogWarning("[UnityMCP] Cannot send editor state - connection closed");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error sending editor state: {e.Message}");
            }
        }

        public object GetEditorState()
        {
            try
            {
                // Wait for any ongoing Unity compilation to finish first
                EditorUtilities.WaitForUnityCompilation();

                var activeGameObjects = new List<string>();
                var selectedObjects = new List<string>();

                // Use FindObjectsByType instead of FindObjectsOfType
                var foundObjects = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                if (foundObjects != null)
                {
                    foreach (var obj in foundObjects)
                    {
                        if (obj != null && !string.IsNullOrEmpty(obj.name))
                        {
                            activeGameObjects.Add(obj.name);
                        }
                    }
                }

                var selection = Selection.gameObjects;
                if (selection != null)
                {
                    foreach (var obj in selection)
                    {
                        if (obj != null && !string.IsNullOrEmpty(obj.name))
                        {
                            selectedObjects.Add(obj.name);
                        }
                    }
                }

                var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var sceneHierarchy = currentScene.IsValid() ? GetSceneHierarchy() : new List<object>();

                var projectStructure = new
                {
                    scenes = GetSceneNames() ?? new string[0],
                    assets = GetAssetPaths() ?? new string[0]
                };

                return new
                {
                    activeGameObjects,
                    selectedObjects,
                    playModeState = EditorApplication.isPlaying ? "Playing" : "Stopped",
                    sceneHierarchy,
                    projectStructure
                };
            }
            catch (Exception e)
            {
                lastErrorMessage = $"Error getting editor state: {e.Message}";
                Debug.LogError(lastErrorMessage);
                return new
                {
                    activeGameObjects = new List<string>(),
                    selectedObjects = new List<string>(),
                    playModeState = "Unknown",
                    sceneHierarchy = new List<object>(),
                    projectStructure = new { scenes = new string[0], assets = new string[0] }
                };
            }
        }

        private object GetSceneHierarchy()
        {
            try
            {
                var roots = new List<object>();
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

                if (scene.IsValid())
                {
                    var rootObjects = scene.GetRootGameObjects();
                    if (rootObjects != null)
                    {
                        foreach (var root in rootObjects)
                        {
                            if (root != null)
                            {
                                try
                                {
                                    roots.Add(GetGameObjectHierarchy(root));
                                }
                                catch (Exception e)
                                {
                                    Debug.LogWarning($"[UnityMCP] Failed to get hierarchy for {root.name}: {e.Message}");
                                }
                            }
                        }
                    }
                }

                return roots;
            }
            catch (Exception e)
            {
                lastErrorMessage = $"Error getting scene hierarchy: {e.Message}";
                Debug.LogError(lastErrorMessage);
                return new List<object>();
            }
        }

        private object GetGameObjectHierarchy(GameObject obj)
        {
            try
            {
                if (obj == null) return null;

                var children = new List<object>();
                var transform = obj.transform;

                if (transform != null)
                {
                    for (int i = 0; i < transform.childCount; i++)
                    {
                        try
                        {
                            var childTransform = transform.GetChild(i);
                            if (childTransform != null && childTransform.gameObject != null)
                            {
                                var childHierarchy = GetGameObjectHierarchy(childTransform.gameObject);
                                if (childHierarchy != null)
                                {
                                    children.Add(childHierarchy);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[UnityMCP] Failed to process child {i} of {obj.name}: {e.Message}");
                        }
                    }
                }

                return new
                {
                    name = obj.name ?? "Unnamed",
                    components = GetComponentNames(obj),
                    children = children
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Failed to get hierarchy for {(obj != null ? obj.name : "null")}: {e.Message}");
                return null;
            }
        }

        private string[] GetComponentNames(GameObject obj)
        {
            try
            {
                if (obj == null) return new string[0];

                var components = obj.GetComponents<Component>();
                if (components == null) return new string[0];

                var validComponents = new List<string>();
                foreach (var component in components)
                {
                    try
                    {
                        if (component != null)
                        {
                            validComponents.Add(component.GetType().Name);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[UnityMCP] Failed to get component name: {e.Message}");
                    }
                }

                return validComponents.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Failed to get component names for {(obj != null ? obj.name : "null")}: {e.Message}");
                return new string[0];
            }
        }

        private static string[] GetSceneNames()
        {
            var scenes = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                scenes.Add(scene.path);
            }
            return scenes.ToArray();
        }

        private static string[] GetAssetPaths()
        {
            var guids = AssetDatabase.FindAssets("", new[] { "Assets/" });
            var paths = new string[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]);
            }
            return paths;
        }
    }
}
