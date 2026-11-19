using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestArucoTracker
{
    /// <summary>
    /// Tracks a cube covered with ArUco markers (4 per face).
    ///
    /// Every <c>Detection Interval</c> seconds, one camera image is copied from the GPU and handed to the
    /// native plugin, which finds the markers and solves the cube pose on a worker thread. The result is
    /// placed in the world using the camera pose that image was taken with. Every detection is used as is:
    /// no prediction and no filtering, only a short glide so the cube doesn't step between detections.
    ///
    /// Cube local space (Unity): +x = right face, +y = top face, -z = front face, +z = rear face.
    /// </summary>
    public class ArUcoCubeTracking : MonoBehaviour
    {
        public enum ArUcoDictionary
        {
            DICT_4X4_50 = 0,
            DICT_4X4_100 = 1,
            DICT_4X4_250 = 2,
            DICT_4X4_1000 = 3
        }

        [Header("Cube")]
        [SerializeField] private ArUcoDictionary _dictionaryId = ArUcoDictionary.DICT_4X4_50;
        [SerializeField, Tooltip("Side length of the cube, in meters (5 cm = 0.05).")]
        private float _cubeSize = 0.05f;
        [SerializeField, Tooltip("Side length of each black marker square, in meters (2 cm = 0.02).")]
        private float _markerSize = 0.02f;
        [SerializeField, Tooltip("Distance from the cube's edge to the nearest marker edge, in meters.")]
        private float _markerBorder = 0.0025f;
        [SerializeField, Tooltip("ID of the first marker. Faces use 4 IDs each in this order: front, right, rear, left, top, bottom. Each face: top-left, top-right, bottom-right, bottom-left.")]
        private int _firstMarkerId = 0;

        [Header("Detection")]
        [SerializeField, Range(0.02f, 1f), Tooltip("Seconds between detections. 0.2 = 5 per second.")]
        private float _detectionInterval = 0.2f;
        [SerializeField, Tooltip("Downscale factor before detection. 1 = full resolution (needed for small markers), 2 = half.")]
        private int _divideNumber = 1;
        [SerializeField, Range(0.01f, 0.2f), Tooltip("Smallest marker to detect: perimeter as a fraction of the largest image side.")]
        private float _minMarkerPerimeterRate = 0.04f;
        [SerializeField, Tooltip("Sub-pixel corner refinement. More accurate poses, slightly slower.")]
        private bool _cornerRefinement = true;
        [SerializeField, Range(1, 6), Tooltip("CPU threads OpenCV may use for detection.")]
        private int _detectionThreads = 3;
        [SerializeField, Tooltip("Flip the image vertically before detection (Unity readbacks are bottom-up).")]
        private bool _flipVertically = true;
        [SerializeField, Tooltip("If no markers are found for a while, also try the image the other way up and keep whichever works.")]
        private bool _autoOrientation = true;

        [Header("Display")]
        [SerializeField, Range(0f, 0.3f), Tooltip("Seconds the shown cube takes to glide to each new detection. 0 = jump straight to it.")]
        private float _glideTime = 0.1f;
        [SerializeField, Tooltip("Hide the cube when it hasn't been detected for this many seconds.")]
        private float _hideAfter = 0.6f;

        private const int FaceCount = 6;
        private const int MarkersPerFace = 4;
        private const int ResultSize = 9; // found, usedMarkers, tx, ty, tz, rx, ry, rz, rmsErrorPx

        // Face layout in the right-handed cube frame used by OpenCV (x -> right face, y -> top face,
        // z -> front face): outward normal, and the face's "right" and "up" directions as printed.
        private static readonly Vector3[] FaceNormals = { new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0) };
        private static readonly Vector3[] FaceRights = { new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 0, 0) };
        private static readonly Vector3[] FaceUps = { new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1), new Vector3(0, 0, 1) };

        private readonly float[] _result = new float[ResultSize];
        private NativeArray<byte> _image;
        private Action<AsyncGPUReadbackRequest> _onReadback;
        private int _imageWidth, _imageHeight;
        private float _fx, _fy, _cx, _cy;
        private bool _isReady;

        private bool _busy;               // an image is being read back or detected (one at a time)
        private bool _submitted;          // ...and it has been handed to the plugin
        private Pose _imageCameraPose;    // camera pose the image in progress was taken with
        private float _lastRequestTime = -1000f;

        private bool _hasPose;
        private Pose _detectedPose;       // latest detection
        private Pose _shownPose;          // what is displayed (glides toward the latest detection)
        private float _lastSeen;

        public float CubeSize => _cubeSize;
        public bool IsReady => _isReady;
        public bool IsCubeTracked => _hasPose;
        /// <summary>Markers used for the latest detection.</summary>
        public int LastMarkersUsed { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string PluginName = "arucobridge";

        [DllImport(PluginName)]
        private static extern int aruco_initialize(int dictionaryId, float minMarkerPerimeterRate, int cornerRefinement,
            int numThreads, int autoOrientation, int boardMarkerCount, int[] boardIds, float[] boardCorners);

        [DllImport(PluginName)]
        private static extern void aruco_shutdown();

        [DllImport(PluginName)]
        private static extern int aruco_submit(IntPtr pixels, int width, int height, int channels, int flipVertically,
            int divide, float fx, float fy, float cx, float cy);

        [DllImport(PluginName)]
        private static extern int aruco_take_result([Out] float[] output, int capacity);
#endif

        public void Initialize(int imageWidth, int imageHeight, float cx, float cy, float fx, float fy)
        {
            Release();

            if (imageWidth <= 0 || imageHeight <= 0 || _divideNumber <= 0 || _cubeSize <= 0 || _markerSize <= 0
                || 2f * (_markerSize + _markerBorder) > _cubeSize + 1e-5f || !SystemInfo.supportsAsyncGPUReadback)
                return;

            _imageWidth = imageWidth;
            _imageHeight = imageHeight;
            float scale = 1f / _divideNumber;
            _fx = fx * scale;
            _fy = fy * scale;
            _cx = cx * scale;
            _cy = cy * scale;

#if UNITY_ANDROID && !UNITY_EDITOR
            _image = new NativeArray<byte>(imageWidth * imageHeight * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _onReadback = OnReadbackComplete;

            BuildBoard(out int[] ids, out float[] corners);
            if (aruco_initialize((int)_dictionaryId, _minMarkerPerimeterRate, _cornerRefinement ? 1 : 0,
                    _detectionThreads, _autoOrientation ? 1 : 0, ids.Length, ids, corners) == 0)
            {
                Release();
                return;
            }
            _isReady = true;
#endif
        }

        /// <summary>
        /// Call once per frame with the camera texture, the camera pose of that image, and whether the
        /// camera delivered a new image this frame.
        /// </summary>
        public void UpdateTracking(Texture cameraTexture, Pose cameraPose, bool isNewCameraFrame)
        {
            if (!_isReady)
                return;

            if (_submitted)
                TakeResult();

            // Start the next detection: one image at a time, at most once per detection interval.
            if (!_busy && isNewCameraFrame && cameraTexture != null
                && Time.time - _lastRequestTime >= _detectionInterval
                && cameraTexture.width == _imageWidth && cameraTexture.height == _imageHeight)
            {
                _busy = true;
                _submitted = false;
                _lastRequestTime = Time.time;
                _imageCameraPose = cameraPose;
                AsyncGPUReadback.RequestIntoNativeArray(ref _image, cameraTexture, 0, _onReadback);
            }

            if (_hasPose && Time.time - _lastSeen > _hideAfter)
                _hasPose = false;

            if (_hasPose)
            {
                float t = _glideTime > 0f ? 1f - Mathf.Exp(-Time.deltaTime / _glideTime) : 1f;
                _shownPose = new Pose(
                    Vector3.Lerp(_shownPose.position, _detectedPose.position, t),
                    Quaternion.Slerp(_shownPose.rotation, _detectedPose.rotation, t));
            }
        }

        /// <summary>World-space pose of the cube's center.</summary>
        public bool TryGetCubePose(out Pose pose)
        {
            pose = _shownPose;
            return _hasPose;
        }

        private void OnDestroy()
        {
            Release();
        }

        private unsafe void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            if (!_isReady)
                return;
            if (request.hasError)
            {
                _busy = false;
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            IntPtr pixels = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(_image);
            _submitted = aruco_submit(pixels, _imageWidth, _imageHeight, 4, _flipVertically ? 1 : 0, _divideNumber,
                _fx, _fy, _cx, _cy) != 0;
#endif
            if (!_submitted)
                _busy = false;
        }

        private void TakeResult()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (aruco_take_result(_result, _result.Length) < ResultSize)
                return; // still detecting

            _busy = false;
            _submitted = false;
            if (_result[0] < 0.5f)
                return; // cube not found in this image

            LastMarkersUsed = Mathf.RoundToInt(_result[1]);

            // OpenCV camera space (y down) -> Unity camera space (y up), then into the world with the
            // camera pose this image was taken with.
            Vector3 cameraSpacePosition = new Vector3(_result[2], -_result[3], _result[4]);
            Quaternion cameraSpaceRotation = ConvertRvecToUnityRotation(_result[5], _result[6], _result[7]);
            _detectedPose = new Pose(
                _imageCameraPose.position + _imageCameraPose.rotation * cameraSpacePosition,
                _imageCameraPose.rotation * cameraSpaceRotation);
            _lastSeen = Time.time;

            if (!_hasPose)
                _shownPose = _detectedPose;
            _hasPose = true;
#endif
        }

        /// <summary>
        /// The 3D corners (TL, TR, BR, BL) of every marker in the right-handed cube frame used by OpenCV,
        /// 12 floats per marker.
        /// </summary>
        private void BuildBoard(out int[] ids, out float[] corners)
        {
            float half = _cubeSize * 0.5f;
            float h = _markerSize * 0.5f;
            float c = half - _markerBorder - h; // marker center offset from the face center
            Vector2[] quadrants = { new Vector2(-c, c), new Vector2(c, c), new Vector2(c, -c), new Vector2(-c, -c) };

            ids = new int[FaceCount * MarkersPerFace];
            corners = new float[ids.Length * 12];
            for (int face = 0; face < FaceCount; face++)
            {
                Vector3 n = FaceNormals[face], u = FaceRights[face], v = FaceUps[face];
                for (int k = 0; k < MarkersPerFace; k++)
                {
                    int m = face * MarkersPerFace + k;
                    ids[m] = _firstMarkerId + m;
                    Vector3 center = n * half + u * quadrants[k].x + v * quadrants[k].y;
                    Vector3[] quad =
                    {
                        center - u * h + v * h, // TL
                        center + u * h + v * h, // TR
                        center + u * h - v * h, // BR
                        center - u * h - v * h  // BL
                    };
                    for (int p = 0; p < 4; p++)
                    {
                        corners[m * 12 + p * 3] = quad[p].x;
                        corners[m * 12 + p * 3 + 1] = quad[p].y;
                        corners[m * 12 + p * 3 + 2] = quad[p].z;
                    }
                }
            }
        }

        private void Release()
        {
            bool wasReady = _isReady;
            _isReady = false;
            _hasPose = false;

#if UNITY_ANDROID && !UNITY_EDITOR
            // Stop the worker first: once this returns, the plugin no longer reads the image.
            if (wasReady)
                aruco_shutdown();
#endif
            // The image must not be freed while the GPU may still write into it.
            if (_busy)
                AsyncGPUReadback.WaitAllRequests();
            _busy = false;
            _submitted = false;

            if (_image.IsCreated)
                _image.Dispose();
        }

        /// <summary>OpenCV Rodrigues rotation (camera space) to a Unity rotation (camera space).</summary>
        private static Quaternion ConvertRvecToUnityRotation(float x, float y, float z)
        {
            float angle = Mathf.Sqrt(x * x + y * y + z * z);
            float r01, r02, r11, r12, r21, r22;

            if (angle < 1e-6f)
            {
                r11 = r22 = 1f;
                r01 = r02 = r12 = r21 = 0f;
            }
            else
            {
                float cosine = Mathf.Cos(angle);
                float sine = Mathf.Sin(angle);
                float oneMinusCosine = 1f - cosine;
                float ax = x / angle, ay = y / angle, az = z / angle;
                r01 = ax * ay * oneMinusCosine - az * sine;
                r02 = ax * az * oneMinusCosine + ay * sine;
                r11 = cosine + ay * ay * oneMinusCosine;
                r12 = ay * az * oneMinusCosine - ax * sine;
                r21 = az * ay * oneMinusCosine + ax * sine;
                r22 = cosine + az * az * oneMinusCosine;
            }

            Vector3 unityUp = new Vector3(r01, -r11, r21);
            Vector3 unityForward = new Vector3(-r02, r12, -r22);
            return Quaternion.LookRotation(unityForward, unityUp);
        }
    }
}
