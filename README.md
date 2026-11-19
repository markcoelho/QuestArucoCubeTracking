# Quest ArUco Cube Tracking

Tracks the position and rotation of a 5 cm cube covered with ArUco markers on Meta Quest 3 / 3S in passthrough mode, and shows a semi-transparent cube on top of the real one. Built on the Quest ArUco Tracking base project (Meta Passthrough Camera API + OpenCV).

---

## The cube

- **Cube:** 5 × 5 × 5 cm
- **Markers:** 24 markers from the `DICT_4X4_50` dictionary, 2 cm wide, 4 per face, each centered in a quarter of the face (2.5 mm from the cube edge, 5 mm between markers)
- **IDs per face** (each face: top-left, top-right, bottom-right, bottom-left):

| Face | IDs | Marker "up" direction |
| --- | --- | --- |
| Front | 0, 1, 2, 3 | toward the top face |
| Right | 4, 5, 6, 7 | toward the top face |
| Rear | 8, 9, 10, 11 | toward the top face |
| Left | 12, 13, 14, 15 | toward the top face |
| Top | 16, 17, 18, 19 | toward the rear face |
| Bottom | 20, 21, 22, 23 | toward the front face |

(Top and bottom read as if you tipped the cube toward you to see the top, and away from you to see the bottom.)

If your cube is different, change **Cube Size**, **Marker Size**, **Marker Border** and **First Marker ID** on the `ArUcoCubeTracking` component.

---

## 1. What you need

- **Unity 6000.3.26f1** (Unity 6.3) with **Android Build Support** installed
  (in Unity Hub: *Installs → ⚙ → Add modules → Android Build Support*, including OpenJDK and Android SDK & NDK)
- A **Meta Quest 3 or 3S**
- A **USB-C cable** to connect the Quest to your PC
- **SideQuest** (Advanced Installer) from [sidequestvr.com/setup-howto](https://sidequestvr.com/setup-howto)
- The marker cube described above

Everything else (Meta XR SDK, OpenCV) is already included in the project.

---

## 2. Build the APK

1. Put the project in a folder with a **short path**, for example `C:\Projects\QuestArucoCubeTracking`. Very long paths make the Android build fail on Windows.
2. Open **Unity Hub → Add → Add project from disk** and select this `QuestArucoCubeTracking` folder.
3. Open the project. The first time takes a few minutes while Unity imports everything.
4. Go to **File → Build Profiles**, select **Android**, and click **Switch Platform** (skip this if Android is already active).
5. In the top menu, click **Build → Build Quest ARM64 APK**.
6. When it finishes, the app is at:

   ```
   QuestArucoCubeTracking/Builds/ArucoCubeTracker.apk
   ```

---

## 3. Install on Quest 3 with SideQuest

### One-time setup: enable Developer Mode

1. Install the **Meta Horizon** app on your phone and sign in with the account the Quest uses.
2. Create a developer organization (free) at [developers.meta.com](https://developers.meta.com/horizon/manage/organizations/create/) if you haven't already.
3. In the phone app: **Devices → your Quest → Headset settings → Developer Mode → On**.
4. Restart the Quest.

### Install the app

1. Open **SideQuest** on your PC.
2. Connect the Quest to the PC with the USB cable.
3. Put on the headset and select **Allow** on the *"Allow USB debugging?"* prompt (tick *Always allow from this computer*).
4. In SideQuest, the dot in the top-left should turn **green** (connected).
5. **Drag and drop `ArucoCubeTracker.apk`** onto the SideQuest window.
   (Or click the *Install APK file from folder* icon at the top and select the APK.)
6. Wait for *"Installed"* in SideQuest's task list.

The cube tracker installs as its own app (**Aruco Cube Tracker**), next to the single-marker **Aruco Tracker**.

### Run it

1. On the Quest, open the **Library** and change the filter to **Unknown Sources**.
2. Launch **Aruco Cube Tracker**.
3. Select **Allow** when it asks for camera access (needed to see the markers).
4. Look at the cube. A semi-transparent cube appears on top of it.

**Controls:** press **A** on the right controller to toggle the camera image view.

---

## How tracking works

1. Each new camera image is copied from the GPU into a reusable buffer.
2. A native C++ plugin (`libarucobridge.so`, built on OpenCV) detects the markers on its own thread.
3. **All detected markers are combined into one pose for the cube.** Every marker corner has a known position on the cube, so one solve over all visible corners gives the cube's center and rotation:
   - One marker is enough to place the cube. More markers, especially on two or three faces, make it much more accurate.
   - A single small marker can be read two ways (tilted one way or the other). The tracker sends its own prediction of the cube pose along, and the plugin keeps the reading that matches it, so the cube doesn't flip.
   - Markers that don't fit the rest, markers seen almost edge-on, and misread duplicate IDs are left out.
4. The pose is placed in the world using the headset pose at the moment the image was taken, smoothed, and predicted forward to the moment the frame is shown, so the overlay doesn't trail behind a moving cube.

## Tuning

Select `ArUcoTrackingAppCoordinator` in the scene.

On the `ArUcoCubeTracking` component:

| Setting | What it does |
| --- | --- |
| **Full Scan Interval** | How often the whole image is searched. Lower = the cube is found again sooner after leaving the view, higher = faster tracking. |
| **Prediction Strength** | How much of the camera delay to predict away (default 0.5). Higher = less drag on smooth moves, lower = calmer on fast shakes and sudden stops. |
| **Max Prediction Distance / Angle** | The furthest the cube may be moved or rotated ahead of its last detection. |
| **Position Noise / Rotation Noise** | Movement smaller than this counts as jitter and is smoothed out. Raise them if a still cube shakes, lower them if small movements feel sluggish. |
| **Still Responsiveness** | How steady a still cube is (lower = steadier). |
| **Velocity Responsiveness** | How quickly the speed estimate reacts. Lower = calmer during jerky motion. |

On the `ArUcoTrackingAppCoordinator` component:

| Setting | What it does |
| --- | --- |
| **Fill Color / Edge Color** | Colors of the overlay (alpha = transparency). |
| **Edge Width** | Width of the brighter edges (0 = none). |
| **Overlay Scale** | Overlay size relative to the real cube. |
| **Attached Object** | Optional object of your own to place at the cube's pose. |

For your own scripts: `ArUcoCubeTracking.TryGetCubePose(out Pose pose)` gives the cube center's world pose. In the cube's local space, +x points to the right face, +y to the top face, and -z to the front face.

## Changing the native plugin

The built plugin is already in `Assets/Plugins/Android/arm64-v8a`. Rebuilding is only needed if you change `NativePlugin/arucobridge.cpp`. Run `NativePlugin/build.bat`. It uses the NDK that comes with Unity and the C++ headers from the [OpenCV 5 Android SDK](https://opencv.org/releases/) (by default it looks for `OpenCV-android-sdk` next to this folder, or set `OPENCV_SDK`).

## Troubleshooting

| Problem | Fix |
| --- | --- |
| SideQuest dot stays red/orange | Re-plug the cable, put the headset on and accept the USB debugging prompt. Check that Developer Mode is on. |
| App not in Library | Set the Library filter to **Unknown Sources**. |
| Cube not detected | Make sure you allowed camera access (Settings → Apps → Permissions) and the room is well lit. |
| Overlay is offset or tilted on some faces | Check that the cube matches the layout table above (IDs, marker direction on top/bottom, 2.5 mm border). Adjust **Marker Border** if your spacing differs. |
| Overlay is the wrong size | Set **Cube Size** and **Marker Size** to your cube, in meters. |
| Build fails with "Cannot run program ... .bat" | The folder path is too long. Move the project to a short path like `C:\Projects\QuestArucoCubeTracking`. |
