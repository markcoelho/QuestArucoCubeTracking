using System.Collections;
using Meta.XR;
using UnityEngine;

namespace QuestArucoTracker
{
    /// <summary>
    /// Feeds passthrough camera frames to <see cref="ArUcoCubeTracking"/> and shows a semi-transparent
    /// cube on top of the real marker cube.
    /// </summary>
    public class ArUcoTrackingAppCoordinator : MonoBehaviour
    {
        [Header("Passthrough Camera")]
        [SerializeField]
        private PassthroughCameraAccess m_passthroughCameraAccess;

        [Header("Cube Tracking")]
        [SerializeField] private ArUcoCubeTracking m_arucoMarkerTracking;
        [SerializeField, Tooltip("Optional: your own object to place at the cube's pose (its pivot goes to the cube center).")]
        private Transform m_attachedObject;
        [SerializeField] MeshRenderer m_debugRenderer;

        [Header("Cube Overlay")]
        [SerializeField, Tooltip("Show the semi-transparent cube over the real cube.")]
        private bool m_showOverlay = true;
        [SerializeField, Tooltip("Fill color of the overlay (alpha = transparency).")]
        private Color m_fillColor = new Color(0.1f, 0.75f, 1f, 0.3f);
        [SerializeField, Tooltip("Color of the overlay's edges.")]
        private Color m_edgeColor = new Color(0.1f, 0.75f, 1f, 0.85f);
        [SerializeField, Range(0f, 0.2f), Tooltip("Edge width as a fraction of a face. 0 = no edges.")]
        private float m_edgeWidth = 0.03f;
        [SerializeField, Range(0.8f, 1.2f), Tooltip("Overlay size relative to the real cube. Slightly above 1 keeps it from hiding inside the real cube.")]
        private float m_overlayScale = 1.01f;

        [Header("Face Numbers")]
        [SerializeField, Tooltip("Show the face numbers 1-6 on the overlay (1 Front, 2 Right, 3 Rear, 4 Left, 5 Top, 6 Bottom).")]
        private bool m_showFaceNumbers = true;
        [SerializeField, Tooltip("Color of the face numbers.")]
        private Color m_faceNumberColor = Color.white;
        [SerializeField, Range(0.1f, 1f), Tooltip("Height of the numbers as a fraction of a face.")]
        private float m_faceNumberSize = 0.5f;

        private GameObject m_overlay;
        private Material m_overlayMaterial;
        private Material m_labelMaterial;
        private Font m_labelFont;
        private bool m_showRecogResult = false;
        private bool m_visible;

        private static readonly int FillColorId = Shader.PropertyToID("_Color");
        private static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");
        private static readonly int EdgeWidthId = Shader.PropertyToID("_EdgeWidth");

        /// <summary>
        /// Waits for the passthrough camera, then initializes cube tracking.
        /// </summary>
        private IEnumerator Start()
        {
            // No logging at all (Unity and plugin C# logs), for performance.
            Debug.unityLogger.logEnabled = false;

            if (m_passthroughCameraAccess == null)
            {
                yield break;
            }

            while (!m_passthroughCameraAccess.IsPlaying)
                yield return null;
            yield return null; // Wait one frame to ensure camera is fully initialized

            InitializeCubeTracking();
            CreateOverlay();
            SetVisible(false);

            if (m_debugRenderer != null)
                m_debugRenderer.gameObject.SetActive(m_showRecogResult);
        }

        private void Update()
        {
            if (m_passthroughCameraAccess == null || !m_passthroughCameraAccess.IsPlaying || !m_arucoMarkerTracking.IsReady)
                return;

            HandleVisualizationToggle();

            //======================================================================================
            // CORE FUNCTIONALITY: hand the newest camera frame (and the camera pose it was taken
            // with) to the tracker. Detection runs on a background thread; this call returns
            // immediately and picks up results whenever they are ready.
            //======================================================================================
            Texture cameraTexture = m_passthroughCameraAccess.GetTexture();
            m_arucoMarkerTracking.UpdateTracking(
                cameraTexture,
                m_passthroughCameraAccess.GetCameraPose(),
                m_passthroughCameraAccess.IsUpdatedThisFrame);

            UpdateCube();

            if (m_showRecogResult && m_debugRenderer != null && m_debugRenderer.material.mainTexture != cameraTexture)
                m_debugRenderer.material.mainTexture = cameraTexture;
        }

        /// <summary>
        /// The A button toggles between the camera image and the cube overlay.
        /// </summary>
        private void HandleVisualizationToggle()
        {
            if (m_debugRenderer == null)
                return;

            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                m_showRecogResult = !m_showRecogResult;
                m_debugRenderer.gameObject.SetActive(m_showRecogResult);
            }
        }

        private void UpdateCube()
        {
            bool tracked = m_arucoMarkerTracking.TryGetCubePose(out Pose pose) && !m_showRecogResult;
            SetVisible(tracked);
            if (!tracked)
                return;

            // Place objects at the latest detected pose.
            if (m_overlay != null)
                m_overlay.transform.SetPositionAndRotation(pose.position, pose.rotation);
            if (m_attachedObject != null)
                m_attachedObject.SetPositionAndRotation(pose.position, pose.rotation);

            if (m_labelMaterial != null)
                m_labelMaterial.color = m_faceNumberColor;

            if (m_overlayMaterial != null)
            {
                m_overlayMaterial.SetColor(FillColorId, m_fillColor);
                m_overlayMaterial.SetColor(EdgeColorId, m_edgeColor);
                m_overlayMaterial.SetFloat(EdgeWidthId, m_edgeWidth);
            }
        }

        private void SetVisible(bool visible)
        {
            if (m_visible == visible)
                return;
            m_visible = visible;
            if (m_overlay != null)
                m_overlay.SetActive(visible && m_showOverlay);
            if (m_attachedObject != null)
                m_attachedObject.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Reads camera intrinsics and initializes the tracker.
        /// </summary>
        private void InitializeCubeTracking()
        {
            var intrinsics = m_passthroughCameraAccess.Intrinsics;
            var cx = intrinsics.PrincipalPoint.x;
            var cy = intrinsics.PrincipalPoint.y;
            var fx = intrinsics.FocalLength.x;
            var fy = intrinsics.FocalLength.y;
            var width = intrinsics.SensorResolution.x;
            var height = intrinsics.SensorResolution.y;

            // Scale parameters to match the current camera resolution
            var currentResolution = m_passthroughCameraAccess.CurrentResolution;
            if (currentResolution.x != width || currentResolution.y != height)
            {
                float scaleX = (float)currentResolution.x / width;
                float scaleY = (float)currentResolution.y / height;
                fx *= scaleX;
                fy *= scaleY;
                cx *= scaleX;
                cy *= scaleY;
                width = currentResolution.x;
                height = currentResolution.y;
            }

            m_arucoMarkerTracking.Initialize(width, height, cx, cy, fx, fy);
        }

        private void CreateOverlay()
        {
            if (m_overlay != null)
                return;

            Shader shader = Resources.Load<Shader>("CubeOverlay");
            if (shader == null || !shader.isSupported)
            {
                return;
            }

            m_overlayMaterial = new Material(shader);
            m_overlay = GameObject.CreatePrimitive(PrimitiveType.Cube);
            m_overlay.name = "Cube Overlay";
            Destroy(m_overlay.GetComponent<Collider>());
            m_overlay.transform.localScale = Vector3.one * (m_arucoMarkerTracking.CubeSize * m_overlayScale);

            MeshRenderer meshRenderer = m_overlay.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = m_overlayMaterial;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            if (m_showFaceNumbers)
                CreateFaceNumbers();
        }

        // Unity cube local space: +x right, +y top, -z front, +z rear.
        // Each face: outward normal and the direction the number's "up" points (same as the markers' up).
        private static readonly Vector3[] LabelNormals = { Vector3.back, Vector3.right, Vector3.forward, Vector3.left, Vector3.up, Vector3.down };
        private static readonly Vector3[] LabelUps = { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.forward, Vector3.back };

        /// <summary>
        /// Adds the numbers 1-6 (Front, Right, Rear, Left, Top, Bottom) as children of the overlay cube.
        /// </summary>
        private void CreateFaceNumbers()
        {
            Shader shader = Resources.Load<Shader>("CubeFaceLabel");
            if (shader == null || !shader.isSupported)
            {
                return;
            }

            const int fontSize = 100;
            m_labelFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_labelFont.RequestCharactersInTexture("123456", fontSize);
            m_labelMaterial = new Material(shader) { mainTexture = m_labelFont.material.mainTexture, color = m_faceNumberColor };
            Font.textureRebuilt += OnFontTextureRebuilt;

            for (int i = 0; i < 6; i++)
            {
                var label = new GameObject("Face " + (i + 1));
                label.transform.SetParent(m_overlay.transform, false);
                // Sit just outside the overlay surface. The text faces its local -z, so point +z into the cube.
                label.transform.localPosition = LabelNormals[i] * 0.505f;
                label.transform.localRotation = Quaternion.LookRotation(-LabelNormals[i], LabelUps[i]);
                // At fontSize 100 and characterSize 1, a line is about 10 units tall.
                label.transform.localScale = Vector3.one * (m_faceNumberSize * 0.1f);

                var text = label.AddComponent<TextMesh>();
                text.font = m_labelFont;
                text.fontSize = fontSize;
                text.characterSize = 1f;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.text = (i + 1).ToString();

                var renderer = label.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = m_labelMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private void OnFontTextureRebuilt(Font font)
        {
            if (font == m_labelFont && m_labelMaterial != null)
                m_labelMaterial.mainTexture = font.material.mainTexture;
        }

        private void OnDestroy()
        {
            if (m_overlayMaterial != null)
                Destroy(m_overlayMaterial);
            if (m_labelMaterial != null)
            {
                Font.textureRebuilt -= OnFontTextureRebuilt;
                Destroy(m_labelMaterial);
            }
        }
    }
}
