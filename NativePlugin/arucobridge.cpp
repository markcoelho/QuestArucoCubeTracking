// Native ArUco cube detector for Unity on Quest (arm64-v8a).
//
// Unity hands over one camera image at a time. A worker thread finds the markers and solves ONE pose for
// the whole cube from every detected marker corner, then Unity picks up the result. Each image is solved
// on its own: nothing is carried over from previous images.
//
// Solving: each detected marker gives two possible cube poses (a single flat square is ambiguous). Every
// candidate is refined over the corners of ALL detected markers and the one that fits best wins. Markers
// that don't fit the winner (misreads) are dropped and the pose is refined once more.
//
// Result (9 floats): [found, usedMarkers, tx, ty, tz, rx, ry, rz, rmsErrorPx]
// Cube pose in OpenCV camera space (x right, y down, z forward), Rodrigues rotation.

#include <algorithm>
#include <array>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <vector>

#include <opencv2/core.hpp>
#include <opencv2/geometry/3d.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/objdetect/aruco_detector.hpp>

#define ARUCO_EXPORT extern "C" __attribute__((visibility("default")))

namespace {

constexpr int kResultSize = 9;
constexpr int kOrientationProbeInterval = 10;  // empty images before the other image orientation is tried
constexpr double kOutlierMinPx = 2.5;          // a marker is dropped above max(this, 3 x median error)

struct Frame {
    const uint8_t* pixels = nullptr;
    int width = 0, height = 0, channels = 0, flip = 0, divide = 1;
    float fx = 0, fy = 0, cx = 0, cy = 0;
};

struct Pose {
    cv::Vec3d rvec, tvec;
    double error = 1e30;  // mean squared reprojection error (px^2)
};

// ---- Shared state (guarded by gMutex) ------------------------------------------------------------
std::mutex gMutex;
std::condition_variable gWake;
std::thread gWorker;
bool gRunning = false;
bool gHasPending = false;
Frame gPending;
bool gHasResult = false;
std::array<float, kResultSize> gResult{};

// ---- Settings (written before the worker starts) -------------------------------------------------
cv::aruco::DetectorParameters gParams;
cv::aruco::Dictionary gDictionary;
bool gAutoOrientation = true;
std::unordered_map<int, std::array<cv::Point3f, 4>> gBoard;  // marker id -> TL, TR, BR, BL in cube space

void PrepareGray(const Frame& f, bool flip, cv::Mat& out, cv::Mat& temp) {
    cv::Mat src(f.height, f.width, f.channels == 4 ? CV_8UC4 : CV_8UC1, const_cast<uint8_t*>(f.pixels),
                static_cast<size_t>(f.width) * f.channels);
    cv::Mat& target = f.divide > 1 ? temp : out;
    if (f.channels == 4) cv::cvtColor(src, target, cv::COLOR_RGBA2GRAY);
    else src.copyTo(target);
    if (flip) cv::flip(target, target, 0);
    if (f.divide > 1) cv::resize(temp, out, cv::Size(f.width / f.divide, f.height / f.divide), 0, 0, cv::INTER_AREA);
}

double MeanSquaredError(const std::vector<cv::Point3f>& object, const std::vector<cv::Point2f>& image,
                        const Pose& p, const cv::Matx33d& K) {
    std::vector<cv::Point2f> projected;
    cv::projectPoints(object, p.rvec, p.tvec, K, cv::noArray(), projected);
    double sum = 0.0;
    for (size_t i = 0; i < projected.size(); i++) {
        const cv::Point2f d = projected[i] - image[i];
        sum += static_cast<double>(d.x) * d.x + static_cast<double>(d.y) * d.y;
    }
    return projected.empty() ? 1e30 : sum / static_cast<double>(projected.size());
}

void Refine(const std::vector<cv::Point3f>& object, const std::vector<cv::Point2f>& image, Pose& p,
            const cv::Matx33d& K) {
    cv::Mat r(p.rvec, false), t(p.tvec, false);  // share memory with the pose
    try {
        cv::solvePnP(object, image, K, cv::noArray(), r, t, true, cv::SOLVEPNP_ITERATIVE);
    } catch (const cv::Exception&) {
    }
    p.error = p.tvec[2] > 0.0 ? MeanSquaredError(object, image, p, K) : 1e30;
}

bool SolveCube(const std::vector<std::vector<cv::Point2f>>& corners, const std::vector<int>& ids, const Frame& f,
               float* out) {
    // Cube markers seen exactly once (an ID seen twice means one copy is a misread).
    std::unordered_map<int, int> seen;
    for (int id : ids) {
        if (gBoard.count(id)) seen[id]++;
    }
    std::vector<int> used;
    for (size_t i = 0; i < ids.size(); i++) {
        auto it = seen.find(ids[i]);
        if (it != seen.end() && it->second == 1) used.push_back(static_cast<int>(i));
    }
    if (used.empty()) return false;

    const cv::Matx33d K(f.fx, 0, f.cx, 0, f.fy, f.cy, 0, 0, 1);
    std::vector<cv::Point3f> object;
    std::vector<cv::Point2f> image;
    auto gather = [&](const std::vector<int>& markers) {
        object.clear();
        image.clear();
        for (int i : markers) {
            const auto& c = gBoard.at(ids[i]);
            object.insert(object.end(), c.begin(), c.end());
            image.insert(image.end(), corners[i].begin(), corners[i].end());
        }
    };
    gather(used);

    // Both possible poses from every marker, each refined over all corners. Best fit wins.
    Pose best;
    std::vector<cv::Mat> rvecs, tvecs;
    cv::Mat r64, t64;
    for (int i : used) {
        const auto& c = gBoard.at(ids[i]);
        const std::vector<cv::Point3f> square(c.begin(), c.end());
        rvecs.clear();
        tvecs.clear();
        int n = 0;
        try {
            n = cv::solvePnPGeneric(square, corners[i], K, cv::noArray(), rvecs, tvecs, false, cv::SOLVEPNP_IPPE);
        } catch (const cv::Exception&) {
            continue;
        }
        n = std::min<int>(n, static_cast<int>(std::min(rvecs.size(), tvecs.size())));
        for (int k = 0; k < n; k++) {
            rvecs[k].convertTo(r64, CV_64F);
            tvecs[k].convertTo(t64, CV_64F);
            Pose p;
            p.rvec = cv::Vec3d(r64.at<double>(0), r64.at<double>(1), r64.at<double>(2));
            p.tvec = cv::Vec3d(t64.at<double>(0), t64.at<double>(1), t64.at<double>(2));
            if (p.tvec[2] <= 0.0) continue;
            Refine(object, image, p, K);
            if (p.error < best.error) best = p;
        }
    }
    if (best.error >= 1e29) return false;

    // Drop markers that don't fit the winning pose, then refine once more.
    if (used.size() > 1) {
        std::vector<double> rms(used.size());
        for (size_t k = 0; k < used.size(); k++) {
            const auto& c = gBoard.at(ids[used[k]]);
            const std::vector<cv::Point3f> square(c.begin(), c.end());
            rms[k] = std::sqrt(MeanSquaredError(square, corners[used[k]], best, K));
        }
        std::vector<double> sorted = rms;
        std::nth_element(sorted.begin(), sorted.begin() + sorted.size() / 2, sorted.end());
        const double threshold = std::max(kOutlierMinPx, 3.0 * sorted[sorted.size() / 2]);
        std::vector<int> kept;
        for (size_t k = 0; k < used.size(); k++) {
            if (rms[k] <= threshold) kept.push_back(used[k]);
        }
        if (!kept.empty() && kept.size() < used.size()) {
            used = kept;
            gather(used);
            Refine(object, image, best, K);
            if (best.error >= 1e29) return false;
        }
    }

    const cv::Vec3d& r = best.rvec;
    const cv::Vec3d& t = best.tvec;
    if (!std::isfinite(t[0] + t[1] + t[2] + r[0] + r[1] + r[2])) return false;

    out[0] = 1.f;
    out[1] = static_cast<float>(used.size());
    out[2] = static_cast<float>(t[0]);
    out[3] = static_cast<float>(t[1]);
    out[4] = static_cast<float>(t[2]);
    out[5] = static_cast<float>(r[0]);
    out[6] = static_cast<float>(r[1]);
    out[7] = static_cast<float>(r[2]);
    out[8] = static_cast<float>(std::sqrt(best.error));
    return true;
}

void WorkerLoop() {
    cv::aruco::ArucoDetector detector(gDictionary, gParams);
    cv::Mat gray, temp;
    std::vector<std::vector<cv::Point2f>> corners;
    std::vector<int> ids;
    int emptyImages = 0;
    int orientation = -1;  // -1 = use the flip Unity asked for; 0/1 = found by trying the other way up

    while (true) {
        Frame f;
        {
            std::unique_lock<std::mutex> lock(gMutex);
            gWake.wait(lock, [] { return !gRunning || gHasPending; });
            if (!gRunning) return;
            f = gPending;
            gHasPending = false;
        }

        std::array<float, kResultSize> result{};
        try {
            const bool flip = orientation >= 0 ? orientation == 1 : f.flip != 0;
            PrepareGray(f, flip, gray, temp);
            detector.detectMarkers(gray, corners, ids);

            // Nothing found for a while: try the image the other way up and keep that if it works.
            if (ids.empty() && gAutoOrientation && ++emptyImages >= kOrientationProbeInterval) {
                emptyImages = 0;
                PrepareGray(f, !flip, gray, temp);
                detector.detectMarkers(gray, corners, ids);
                if (!ids.empty()) orientation = flip ? 0 : 1;
            }
            if (!ids.empty()) emptyImages = 0;

            if (!SolveCube(corners, ids, f, result.data())) result.fill(0.f);
        } catch (...) {
            result.fill(0.f);
        }

        {
            std::lock_guard<std::mutex> lock(gMutex);
            gResult = result;
            gHasResult = true;
        }
    }
}

void StopWorker() {
    {
        std::lock_guard<std::mutex> lock(gMutex);
        gRunning = false;
        gHasPending = false;
    }
    gWake.notify_all();
    if (gWorker.joinable()) gWorker.join();
    std::lock_guard<std::mutex> lock(gMutex);
    gHasResult = false;
}

}  // namespace

// Creates the detector and starts the worker. `boardCorners` holds 12 floats per marker: x, y, z of its
// TL, TR, BR, BL corners in cube coordinates (meters, right-handed). Calling again restarts.
ARUCO_EXPORT int aruco_initialize(int dictionaryId, float minMarkerPerimeterRate, int cornerRefinement,
                                  int numThreads, int autoOrientation,
                                  int boardMarkerCount, const int* boardIds, const float* boardCorners) {
    if (dictionaryId < cv::aruco::DICT_4X4_50 || dictionaryId > cv::aruco::DICT_4X4_1000) return 0;
    if (boardMarkerCount <= 0 || boardIds == nullptr || boardCorners == nullptr) return 0;

    StopWorker();
    try {
        cv::setNumThreads(std::max(1, numThreads));

        cv::aruco::DetectorParameters p;
        p.adaptiveThreshWinSizeMin = 7;
        p.adaptiveThreshWinSizeMax = 17;
        p.adaptiveThreshWinSizeStep = 10;
        p.minMarkerPerimeterRate = minMarkerPerimeterRate;
        p.minDistanceToBorder = 3;
        p.cornerRefinementMethod = cornerRefinement ? cv::aruco::CORNER_REFINE_SUBPIX : cv::aruco::CORNER_REFINE_NONE;
        p.cornerRefinementWinSize = 3;
        p.useAruco3Detection = false;  // downsamples internally and loses small markers
        gParams = p;
        gDictionary = cv::aruco::getPredefinedDictionary(dictionaryId);
        gAutoOrientation = autoOrientation != 0;

        gBoard.clear();
        for (int i = 0; i < boardMarkerCount; i++) {
            std::array<cv::Point3f, 4> c;
            for (int k = 0; k < 4; k++) {
                const float* v = boardCorners + i * 12 + k * 3;
                c[k] = cv::Point3f(v[0], v[1], v[2]);
            }
            gBoard[boardIds[i]] = c;
        }
    } catch (...) {
        return 0;
    }

    {
        std::lock_guard<std::mutex> lock(gMutex);
        gRunning = true;
        gHasPending = false;
        gHasResult = false;
    }
    gWorker = std::thread(WorkerLoop);
    return 1;
}

// Stops the worker. When this returns, the plugin no longer reads any image.
ARUCO_EXPORT void aruco_shutdown() {
    StopWorker();
}

// Queues one image for detection. `pixels` must stay valid until its result has been taken.
// fx, fy, cx, cy are the camera intrinsics for the (possibly downscaled) detection image.
ARUCO_EXPORT int aruco_submit(const void* pixels, int width, int height, int channels, int flipVertically,
                              int divide, float fx, float fy, float cx, float cy) {
    if (pixels == nullptr || width <= 0 || height <= 0 || (channels != 1 && channels != 4) || fx <= 0 || fy <= 0) {
        return 0;
    }
    {
        std::lock_guard<std::mutex> lock(gMutex);
        if (!gRunning) return 0;
        Frame& f = gPending;
        f.pixels = static_cast<const uint8_t*>(pixels);
        f.width = width;
        f.height = height;
        f.channels = channels;
        f.flip = flipVertically;
        f.divide = std::max(1, divide);
        f.fx = fx;
        f.fy = fy;
        f.cx = cx;
        f.cy = cy;
        gHasPending = true;
    }
    gWake.notify_one();
    return 1;
}

// Copies the result of the last submitted image into `out`. Returns kResultSize, or 0 if not ready yet.
ARUCO_EXPORT int aruco_take_result(float* out, int capacity) {
    std::lock_guard<std::mutex> lock(gMutex);
    if (!gHasResult || out == nullptr || capacity < kResultSize) return 0;
    gHasResult = false;
    std::copy(gResult.begin(), gResult.end(), out);
    return kResultSize;
}
