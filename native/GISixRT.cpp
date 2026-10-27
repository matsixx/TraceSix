// TraceSixRT.dll (source file GISixRT.cpp) - TraceSix's hardware ray tracing, phase 0 (2026-10-15).
//
// Tarkov renders with D3D11; hardware ray tracing is D3D12 (DXR). As the FSR4 bridge does, this keeps a private
// D3D12 device on the game's adapter beside Unity's D3D11 one, swaps textures with it through shared resources
// and syncs the two through one shared fence (the waits are on the GPU; the CPU does not block in a frame).
//
// Route B (per-pixel ray-traced light): the map's static meshes (GISix's own list, the one its voxel world is
// built from, plus the terrain as triangles) are copied into ray tracing structures; every frame GISix's
// RTRAYS pass makes rays from the picture, this traces them (GISixRTTrace.hlsl, inline ray queries) and hands
// back where each ended, the facing there and whether the sun reaches it, and GISix's RTSHADE pass lights the
// ends. What it reports: how long the copy and the build take, what they hold in video memory, and what a
// frame's rays cost on the GPU (timestamps around the dispatch). Phase 0 (0.24.61) measured this on Customs.
//
// Called from C# (GISix's GiRt.cs) through exports and render-thread events (IssuePluginEvent): event 1 builds
// the scene, 3 lets it go, 16 + slot traces a frame with the parameters C# left in that slot. Nothing here
// touches Unity's API; it only takes D3D11 resources' pointers.

#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <array>
#include <string>
#include <vector>

#include "build/GISixRTTrace.h"   // g_GISixRTTrace: the trace shader as DXIL (build.bat compiles it)
#include "build/GISixRTAo.h"      // g_GISixRTAo: its CSAo, the ray-traced ambient occlusion (0.24.86)
#include "build/GISixRTRefl.h"    // g_GISixRTRefl: its CSRefl, SSRSix's off-screen reflection rays (0.24.87)
#include "build/GISixRTSun.h"     // g_GISixRTSun: its CSSun, the sun's shadow for the picture (0.24.89)

using Microsoft::WRL::ComPtr;
#define RT_EXPORT extern "C" __declspec(dllexport)

//--- log, drained by C# ---------------------------------------------------------------------------------
static std::mutex gLogMutex;
static std::deque<std::string> gLogLines;
static void Log(const char* fmt, ...)
{
    char buf[1536];
    va_list a;
    va_start(a, fmt);
    vsnprintf(buf, sizeof(buf), fmt, a);
    va_end(a);
    std::lock_guard<std::mutex> l(gLogMutex);
    if (gLogLines.size() > 256) gLogLines.pop_front();
    gLogLines.emplace_back(buf);
}
// (0.24.159) the every-300-frames GPU time / video memory lines only with C#'s "Debug Log" (RtSetVerbose); the numbers
// still go to gStats either way
static volatile int gVerbose = 0;
static double NowMs()
{
    static LARGE_INTEGER f = {};
    if (!f.QuadPart) QueryPerformanceFrequency(&f);
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return 1000.0 * (double)t.QuadPart / (double)f.QuadPart;
}

//--- status ---------------------------------------------------------------------------------------------
enum { RT_NONE = 0, RT_READY = 1, RT_NO_DEVICE = -1, RT_NO_DXR = -2, RT_NO_INTEROP = -3, RT_BUILD_FAILED = -4, RT_CRASHED = -5 };
static std::atomic<int> gStatus{RT_NONE};
static std::atomic<int> gSceneReady{0};
static std::recursive_mutex gRender;
static float gStats[24];   // read by C# (RtGetStats): see BuildScene / Trace

//--- devices --------------------------------------------------------------------------------------------
static ComPtr<ID3D11Device> g11;
static ComPtr<ID3D11Device1> g11_1;
static ComPtr<ID3D11Device5> g11_5;
static ComPtr<ID3D11DeviceContext> gCtx11;
static ComPtr<ID3D11DeviceContext4> gCtx11_4;
static ComPtr<ID3D12Device5> g12;
// (0.24.149) The video memory read-out: this process's use (the game's D3D11 and this side together) against the budget
// Windows gives it - past the budget Windows moves memory out to system RAM, which stalls frames. Sampled every trace.
static ComPtr<IDXGIAdapter3> gAdapter3;
static double gVramPeak = 0.0, gVramSysPeak = 0.0, gVramBudgetMin = 0.0;
static int gVramOver = 0, gVramN = 0;

static bool VramNow(double& usedGB, double& budgetGB, double& sysGB)
{
    if (!gAdapter3) return false;
    DXGI_QUERY_VIDEO_MEMORY_INFO l = {}, n = {};
    if (FAILED(gAdapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &l))) return false;
    if (FAILED(gAdapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &n))) n.CurrentUsage = 0;
    const double gb = 1.0 / (1024.0 * 1024.0 * 1024.0);
    usedGB = (double)l.CurrentUsage * gb;
    budgetGB = (double)l.Budget * gb;
    sysGB = (double)n.CurrentUsage * gb;
    return true;
}

// (1.0.0) The video-memory check before the scene build (RoomFor: no ray tracing on a map that would go too far over the
// budget) is REMOVED, his call: it was a guess at his players' AMD crashes that their logs ruled out (19 GB budget, 9 used),
// and its only firing was a false refusal on his own 9070 XT (Windows gives it ~10.8 GB). VramNow's log lines stay.
static ComPtr<ID3D12CommandQueue> gQueue;     // the scene builds
static ComPtr<ID3D12CommandQueue> gCompute;   // the traces (0.24.76: a compute queue, which the GPU can run beside D3D11's drawing)
static ComPtr<ID3D12Fence> gFence12;
static ComPtr<ID3D11Fence> gFence11;
static UINT64 gFenceValue = 0;
static HANDLE gFenceEvent = nullptr;

// (1.0.0, his players' AMD driver crashes and freezes with ray tracing on) Our device lost - a GPU reset (TDR), the driver's
// or ours - or our GPU work stalled for good: the ray tracing stops for the session. Nothing is rebuilt (seconds of GPU work
// straight after a reset is how one reset becomes the next) and nothing more is sent or waited for (OnRenderEvent).
static bool DeviceLost() { return g12 && FAILED(g12->GetDeviceRemovedReason()); }
static bool gGaveUp = false;
// (1.0.0, his players' RDNA3 cards - 7900 XT, 7800 XT - stall on the first traced frame; NVIDIA and RDNA4 do not) DRED, the
// D3D12 runtime's own crash record: switched on before our device is made (it holds for D3D12 devices made after it in this
// process), read when our work stalls or our device is lost - each command list that had not finished and the step it was at
// (auto-breadcrumbs), and for a page fault the address and the named resources round it. It has nothing to give while the
// device stands, so a stall our 10 s wait gave up on removes our device first (RemoveDevice: the ray tracing is over for the
// session anyway; a removed device's fences read as done, which may also free the game's own wait on ours). Written to the log
// AND to TraceSixRT-gpu-report.txt beside this DLL: the game may not live long enough for C# to collect the log lines.
static bool gDredOn = false;
static void Name(ID3D12Object* o, const wchar_t* n) { if (o) o->SetName(n); }
static void DredEnable()
{
    static bool tried = false;
    if (tried) return;
    tried = true;
    ComPtr<ID3D12DeviceRemovedExtendedDataSettings> s;
    if (FAILED(D3D12GetDebugInterface(IID_PPV_ARGS(&s)))) { Log("[TraceSixRT] GPU crash report (DRED): not available on this system."); return; }
    s->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
    s->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
    gDredOn = true;
}
static const char* DredOp(D3D12_AUTO_BREADCRUMB_OP op)
{
    switch (op)
    {
    case D3D12_AUTO_BREADCRUMB_OP_DISPATCH: return "Dispatch (a trace)";
    case D3D12_AUTO_BREADCRUMB_OP_COPYBUFFERREGION: return "CopyBufferRegion";
    case D3D12_AUTO_BREADCRUMB_OP_COPYTEXTUREREGION: return "CopyTextureRegion";
    case D3D12_AUTO_BREADCRUMB_OP_COPYRESOURCE: return "CopyResource";
    case D3D12_AUTO_BREADCRUMB_OP_RESOURCEBARRIER: return "ResourceBarrier";
    case D3D12_AUTO_BREADCRUMB_OP_BUILDRAYTRACINGACCELERATIONSTRUCTURE: return "BuildRaytracingAccelerationStructure";
    case D3D12_AUTO_BREADCRUMB_OP_COPYRAYTRACINGACCELERATIONSTRUCTURE: return "CopyRaytracingAccelerationStructure";
    case D3D12_AUTO_BREADCRUMB_OP_EMITRAYTRACINGACCELERATIONSTRUCTUREPOSTBUILDINFO: return "EmitRaytracingAccelerationStructurePostbuildInfo";
    case D3D12_AUTO_BREADCRUMB_OP_RESOLVEQUERYDATA: return "ResolveQueryData";
    case D3D12_AUTO_BREADCRUMB_OP_CLEARUNORDEREDACCESSVIEW: return "ClearUnorderedAccessView";
    default: return "another step";
    }
}
static FILE* gReport = nullptr;
static void ReportLine(const char* fmt, ...)
{
    char buf[1024];
    va_list a;
    va_start(a, fmt);
    vsnprintf(buf, sizeof(buf), fmt, a);
    va_end(a);
    Log("%s", buf);
    if (gReport) { fprintf(gReport, "%s\n", buf); fflush(gReport); }
}
static void DredReport(const char* why)
{
    wchar_t path[MAX_PATH] = {};
    HMODULE m = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&DredReport, &m)
        && GetModuleFileNameW(m, path, MAX_PATH))
    {
        wchar_t* s = wcsrchr(path, L'\\');
        if (s) { wcscpy_s(s + 1, MAX_PATH - (s + 1 - path), L"TraceSixRT-gpu-report.txt"); _wfopen_s(&gReport, path, L"w"); }
    }
    if (gReport)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fprintf(gReport, "TraceSixRT GPU report, %04d-%02d-%02d %02d:%02d:%02d. %s.\n", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, why);
        if (gAdapter3) { DXGI_ADAPTER_DESC ad = {}; gAdapter3->GetDesc(&ad); fprintf(gReport, "GPU: %ls\n", ad.Description); }
    }
    if (!gDredOn || !g12) ReportLine("[TraceSixRT] GPU crash report: DRED was not on.");
    else
    {
        ComPtr<ID3D12DeviceRemovedExtendedData> dred;
        if (FAILED(g12.As(&dred))) ReportLine("[TraceSixRT] GPU crash report: no DRED interface on this device.");
        else
        {
            D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT bc = {};
            HRESULT hb = dred->GetAutoBreadcrumbsOutput(&bc);
            if (FAILED(hb)) ReportLine("[TraceSixRT] GPU crash report: no step record (0x%08lX).", (unsigned long)hb);
            else
            {
                int shown = 0;
                for (const D3D12_AUTO_BREADCRUMB_NODE* n = bc.pHeadAutoBreadcrumbNode; n && shown < 8; n = n->pNext)
                {
                    UINT32 done = n->pLastBreadcrumbValue ? *n->pLastBreadcrumbValue : 0;
                    if (done >= n->BreadcrumbCount) continue;   // finished
                    shown++;
                    ReportLine("[TraceSixRT] GPU crash report: command list '%ls' (queue '%ls') stopped after %u of its %u steps; the next: %s.",
                               n->pCommandListDebugNameW ? n->pCommandListDebugNameW : L"?", n->pCommandQueueDebugNameW ? n->pCommandQueueDebugNameW : L"?",
                               done, n->BreadcrumbCount, n->pCommandHistory ? DredOp(n->pCommandHistory[done]) : "?");
                }
                if (shown == 0) ReportLine("[TraceSixRT] GPU crash report: every command list it recorded had finished.");
            }
            D3D12_DRED_PAGE_FAULT_OUTPUT pf = {};
            HRESULT hp = dred->GetPageFaultAllocationOutput(&pf);
            if (SUCCEEDED(hp) && pf.PageFaultVA != 0)
            {
                ReportLine("[TraceSixRT] GPU crash report: page fault at GPU address 0x%llX.", (unsigned long long)pf.PageFaultVA);
                int k = 0;
                for (const D3D12_DRED_ALLOCATION_NODE* a = pf.pHeadExistingAllocationNode; a && k < 8; a = a->pNext, k++)
                    ReportLine("[TraceSixRT]   near it (in use): %ls", a->ObjectNameW ? a->ObjectNameW : L"(unnamed)");
                k = 0;
                for (const D3D12_DRED_ALLOCATION_NODE* a = pf.pHeadRecentFreedAllocationNode; a && k < 8; a = a->pNext, k++)
                    ReportLine("[TraceSixRT]   near it (freed just before): %ls", a->ObjectNameW ? a->ObjectNameW : L"(unnamed)");
            }
            else ReportLine("[TraceSixRT] GPU crash report: no page fault recorded.");
        }
    }
    if (gReport) { fclose(gReport); gReport = nullptr; }
}
static void GiveUp(const char* why)
{
    if (gStatus == RT_CRASHED) return;
    Log("[TraceSixRT] %s: the ray tracing is off until the game restarts (TraceSix's voxel light takes over).", why);
    gGaveUp = true;
    gStatus = RT_CRASHED;
    gSceneReady = 0;
    // (1.0.0) the GPU report (DRED): a stall first removes our device, or DRED has nothing to give
    if (g12 && !DeviceLost()) g12->RemoveDevice();
    DredReport(why);
}

// The CPU waits until the D3D12 side has reached v. Until it is reached, or our device is lost, or 10 s have gone by (a hang the
// driver did not reset). false: not reached - the caller must then NOT reset or free what that work uses. (To 1.0.0 these waits
// gave up after 1 - 20 s and went on: command memory the GPU was still running was reset or freed - on AMD a page fault, a driver
// crash - whenever the GPU fell that far behind; video memory paging on an 8 GB card does it.)
static bool WaitFenceCpu(UINT64 v)
{
    if (!gFence12) return false;
    for (int waited = 0; gFence12->GetCompletedValue() < v; waited += 250)
    {
        if (DeviceLost() || waited >= 10000) return false;
        gFence12->SetEventOnCompletion(v, gFenceEvent);
        WaitForSingleObject(gFenceEvent, 250);
    }
    return !DeviceLost();
}

// Both queues done with everything sent so far - one after the other, so the fence they share only rises. false: they are
// not (GiveUp has been called; leave everything they use alone).
static bool WaitIdle()
{
    if (!gFence12) return true;
    for (ID3D12CommandQueue* q : { gCompute.Get(), gQueue.Get() })
    {
        if (!q) continue;
        UINT64 v = ++gFenceValue;
        q->Signal(gFence12.Get(), v);
        if (!WaitFenceCpu(v)) { GiveUp(DeviceLost() ? "Our D3D12 device was lost" : "The GPU did not finish our work within 10 s"); return false; }
    }
    return true;
}

static void DropScene();
static void DropPipeline();

static bool EnsureDevices(ID3D11Resource* anyUnityResource)
{
    if (!anyUnityResource) return false;
    ComPtr<ID3D11Device> cur;
    anyUnityResource->GetDevice(&cur);
    if (g12 && cur.Get() == g11.Get() && SUCCEEDED(g12->GetDeviceRemovedReason())) return true;
    if (gStatus == RT_NO_DXR || gStatus == RT_NO_INTEROP || gStatus == RT_NO_DEVICE || gStatus == RT_CRASHED) return false;   // said once, not every frame
    // (1.0.0) ours lost: not made again (GiveUp); only a new D3D11 device of the game's, ours alive, starts over
    if (DeviceLost())
    {
        char why[96];
        snprintf(why, sizeof(why), "Our D3D12 device was lost (0x%08lX)", (unsigned long)g12->GetDeviceRemovedReason());
        GiveUp(why);
        return false;
    }
    if (g12)
    {
        Log("[TraceSixRT] the game's D3D11 device changed or ours was lost (0x%08lX): starting over, the scene must be built again",
            (unsigned long)g12->GetDeviceRemovedReason());
        DropScene();
        DropPipeline();
        gQueue.Reset(); gCompute.Reset(); gFence12.Reset(); gFence11.Reset(); g12.Reset();
    }
    g11 = cur;
    g11->GetImmediateContext(&gCtx11);
    if (FAILED(g11.As(&g11_1)) || FAILED(g11.As(&g11_5)) || FAILED(gCtx11.As(&gCtx11_4)))
    {
        Log("[TraceSixRT] D3D11.4 interfaces missing: no fence interop on this system");
        gStatus = RT_NO_INTEROP;
        return false;
    }
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(g11.As(&dxgi)) || FAILED(dxgi->GetAdapter(&adapter))) { gStatus = RT_NO_DEVICE; return false; }
    if (FAILED(adapter.As(&gAdapter3))) gAdapter3.Reset();
    double vu0 = 0.0, vb0 = 0.0, vs0 = 0.0;
    bool vram0 = VramNow(vu0, vb0, vs0);
    DredEnable();   // (1.0.0) before the device: DRED holds for devices made after it
    ComPtr<ID3D12Device> dev;
    HRESULT hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev));
    if (FAILED(hr) || FAILED(dev.As(&g12)))
    {
        Log("[TraceSixRT] D3D12CreateDevice failed (0x%08lX)", (unsigned long)hr);
        gStatus = RT_NO_DEVICE;
        return false;
    }
    D3D12_FEATURE_DATA_D3D12_OPTIONS5 o5 = {};
    g12->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &o5, sizeof(o5));
    DXGI_ADAPTER_DESC ad = {};
    adapter->GetDesc(&ad);
    char name[128] = {};
    WideCharToMultiByte(CP_UTF8, 0, ad.Description, -1, name, sizeof(name) - 1, nullptr, nullptr);
    if (o5.RaytracingTier < D3D12_RAYTRACING_TIER_1_1)
    {
        Log("[TraceSixRT] '%s' has no ray tracing tier 1.1 (tier %d): no hardware rays on this GPU / driver", name, (int)o5.RaytracingTier);
        g12.Reset();
        gStatus = RT_NO_DXR;
        return false;
    }
    D3D12_COMMAND_QUEUE_DESC qd = {};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    g12->CreateCommandQueue(&qd, IID_PPV_ARGS(&gQueue));
    qd.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    g12->CreateCommandQueue(&qd, IID_PPV_ARGS(&gCompute));
    Name(gQueue.Get(), L"TraceSix build queue");
    Name(gCompute.Get(), L"TraceSix trace queue");
    g12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&gFence12));
    HANDLE h = nullptr;
    if (!gQueue || !gCompute || !gFence12 || FAILED(g12->CreateSharedHandle(gFence12.Get(), nullptr, GENERIC_ALL, nullptr, &h)) ||
        FAILED(g11_5->OpenSharedFence(h, IID_PPV_ARGS(&gFence11))))
    {
        if (h) CloseHandle(h);
        Log("[TraceSixRT] could not share a fence between D3D12 and D3D11");
        gStatus = RT_NO_INTEROP;
        return false;
    }
    CloseHandle(h);
    gFenceValue = 0;
    if (!gFenceEvent) gFenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    Log("[TraceSixRT] D3D12 device on '%s', ray tracing tier %d.%d", name, (int)o5.RaytracingTier / 10, (int)o5.RaytracingTier % 10);
    double vu1 = 0.0, vb1 = 0.0, vs1 = 0.0;
    if (vram0 && VramNow(vu1, vb1, vs1))
        Log("[TraceSixRT] Video memory of this process: %.2f GB before the D3D12 device, %.2f GB with it, of a %.2f GB budget (%.2f GB moved out to system memory).",
            vu0, vu1, vb1, vs1);
    else if (!gAdapter3) Log("[TraceSixRT] Video memory: no DXGI 1.4 adapter interface, no read-out.");
    return true;
}

//--- buffers --------------------------------------------------------------------------------------------
static ComPtr<ID3D12Resource> MakeBuffer(UINT64 size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_STATES state, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE)
{
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = heap;
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    rd.Width = size < 256 ? 256 : size;
    rd.Height = 1;
    rd.DepthOrArraySize = 1;
    rd.MipLevels = 1;
    rd.SampleDesc = {1, 0};
    rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    rd.Flags = flags;
    ComPtr<ID3D12Resource> r;
    if (FAILED(g12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, state, nullptr, IID_PPV_ARGS(&r)))) return nullptr;
    return r;
}

static void UavBarrier(ID3D12GraphicsCommandList4* l, ID3D12Resource* r)
{
    D3D12_RESOURCE_BARRIER u = {};
    u.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
    u.UAV.pResource = r;
    l->ResourceBarrier(1, &u);
}

static void Transition(ID3D12Resource* r, D3D12_RESOURCE_STATES a, D3D12_RESOURCE_STATES b, D3D12_RESOURCE_BARRIER& out)
{
    out = {};
    out.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    out.Transition.pResource = r; out.Transition.StateBefore = a; out.Transition.StateAfter = b;
    out.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
}

// One command list for the build (the CPU waits on it: the build happens once).
static ComPtr<ID3D12CommandAllocator> gBuildAlloc;
static ComPtr<ID3D12GraphicsCommandList4> gBuildList;
static void BeginBuildList()
{
    if (!gBuildAlloc) g12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&gBuildAlloc));
    else gBuildAlloc->Reset();
    if (!gBuildList) { g12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, gBuildAlloc.Get(), nullptr, IID_PPV_ARGS(&gBuildList)); Name(gBuildList.Get(), L"TraceSix scene build"); }
    else gBuildList->Reset(gBuildAlloc.Get(), nullptr);
}
// (false: the GPU did not get through it - GiveUp has been called; the build stops and frees nothing)
static bool SubmitBuildListAndWait()
{
    gBuildList->Close();
    ID3D12CommandList* l[] = { gBuildList.Get() };
    gQueue->ExecuteCommandLists(1, l);
    return WaitIdle();
}

// A D3D12 texture shared into D3D11 (see EnsureShared, with the per-frame trace).
struct SharedTex { ComPtr<ID3D12Resource> res12; ComPtr<ID3D11Texture2D> tex11; void* unity = nullptr; UINT w = 0, h = 0; DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN; };
static bool EnsureShared(SharedTex& st, void* unityPtr, bool uav, const char* label);

//--- the scene (C# hands it over on the main thread; event 1 builds it on the render thread) ------------
#pragma pack(push, 4)
struct MeshDesc
{
    void* vb;          // ID3D11Buffer* of the stream that holds the positions
    void* ib;          // ID3D11Buffer* of the indices
    int stride;        // that stream's vertex size
    int posOffset;     // the position's offset in it
    int posFormat;     // 0 = 3 x float32, 1 = 3 (or 4) x float16
    int vertexCount;
    int index32;       // 1 = 32-bit indices, 0 = 16-bit
    int firstSub;      // its submeshes in the submesh list: [firstSub, firstSub + subCount)
    int subCount;
    int uvStride;      // (0.24.89) the texture coordinate's stream's vertex size,
    int uvOffset;      //   its offset in it,
    int uvFormat;      //   0 = none, 1 = 2 x float32, 2 = 2 x float16, 3 = 2 x unorm16
    void* uvVb;        //   and that stream's ID3D11Buffer* (the position's own, or another)
    int colStride;     // (0.24.94) the vertex colour (the painted materials' layer weights): its stream's vertex size,
    int colOffset;     //   its offset in it,
    int colFormat;     //   0 = none, 1 = 4 x unorm8, 2 = 4 x float32, 3 = 4 x float16, 4 = 4 x unorm16
    void* colVb;       //   and that stream's ID3D11Buffer*
};
#pragma pack(pop)
// flags: 1 = alpha-tested (traced as partly see-through), 2 = terrain (one-sided); tile: its material's tile in the atlas (-1 none)
struct SubDesc { int indexStart; int indexCount; int baseVertex; int flags; int tile; };
static std::vector<MeshDesc> gMeshIn;
static std::vector<SubDesc> gSubIn;
static std::vector<int> gInstMeshIn;
static std::vector<float> gInstMatIn;   // 12 per instance: rows 0-2 of the object-to-world matrix
static std::vector<int> gInstFlagsIn;   // per instance: 1 = part of a lamp's own fixture
// (0.24.102) Material variants: copies of a mesh placed with other materials than its first copy (a red dumpster, a
// blue one). Each variant has its own geometry-table rows - the mesh's, with its own tiles - and its instances point
// there; the BLAS stays the mesh's. varMesh / varFirst per variant (its tiles at varTiles[varFirst..], one per listed
// submesh of its mesh), instVar per instance (-1 = the mesh's own rows).
static std::vector<int> gVarMeshIn, gVarFirstIn, gVarTilesIn, gInstVarIn;
static std::mutex gSceneMutex;

static ComPtr<ID3D12Resource> gPos, gIdx, gTlas, gInstBuf;
// (0.24.81) Doors: the top level is built to be refreshed (ALLOW_UPDATE). C# sends the placed instances that moved
// (RtUpdateInstances); the next trace writes their transforms into gInstBuf and refreshes gTlas in place first.
static ComPtr<ID3D12Resource> gTlasUpd;                 // the refresh's scratch
static D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS gTlasIn = {};
static std::vector<int> gInstSlot;                      // C#'s instance index -> its slot in gInstBuf (-1: not placed)
static std::mutex gInstMutex;
static std::vector<std::pair<int, std::array<float, 12>>> gInstPending;
static int gRefreshes = 0;
// (0.24.82) The instances live in a default-heap buffer; a refresh copies the moved transforms in from a small upload
// buffer of the trace's ring slot (free again by then) - on the GPU, in order: the CPU never waits for the GPU (0.24.81
// waited for the last refresh before rewriting the upload-heap list: a stall each time a door moved anywhere).
static ComPtr<ID3D12Resource> gInstDef;
static const int kMaxMoved = 1024;
static ComPtr<ID3D12Resource> gInstUp[8];
static ComPtr<ID3D12Resource> gGeoBuf;   // per geometry: first index, first vertex, material tile, flags (the trace reads a hit's triangle through it)
// (0.24.89) Materials: every vertex's texture coordinate (float2, alongside gPos), the tile table (2 float4 a tile) and the
// two atlases C# drew (RtSetMaterials; shared from D3D11 at the build, then only read here).
static ComPtr<ID3D12Resource> gUV, gTileBuf;
static ComPtr<ID3D12Resource> gCol;   // (0.24.94) every vertex's colour (RGBA8, alongside gPos): the painted materials' layer weights
static SharedTex gAtlasTex, gAtlasLowTex;
static void* gMatAtlasIn = nullptr;
static void* gMatAtlasLowIn = nullptr;
static int gMatTileIn = 0, gMatColsIn = 0;
static std::vector<float> gMatTilesIn;
static int gMatTile = 0, gMatCols = 0, gMatTiles = 0;
// (0.24.155) floats per tile in the table (three float4s: the kernel's gTiles[3 t .. 3 t + 2]): + the glow's tile and strength
static const int kTileFloats = 12;
static bool gDescDirty = false;   // the build changed what the descriptors name: rewritten before the next trace
static int gAtlasGen = 0;         // (0.24.120) raised with every material upload: the scope's table names the atlases too
static std::vector<ComPtr<ID3D12Resource>> gBlasPools;   // the bottom-level structures, a buffer per build batch

static void DropScene()
{
    gSceneReady = 0;
    gTlas.Reset(); gInstBuf.Reset(); gBlasPools.clear(); gPos.Reset(); gIdx.Reset(); gGeoBuf.Reset();
    gUV.Reset(); gTileBuf.Reset(); gCol.Reset(); gAtlasTex = SharedTex{}; gAtlasLowTex = SharedTex{}; gMatTile = gMatCols = gMatTiles = 0; gDescDirty = true;
    gTlasUpd.Reset(); gInstSlot.clear(); gRefreshes = 0; gInstDef.Reset();
    for (auto& u : gInstUp) u.Reset();
    std::lock_guard<std::mutex> l(gInstMutex);
    gInstPending.clear();
}

static float Half(UINT16 h)
{
    UINT32 s = (UINT32)(h & 0x8000u) << 16, e = (h >> 10) & 0x1fu, m = h & 0x3ffu, f;
    if (e == 0) f = s;                                       // zero (and subnormals: far below a millimetre)
    else if (e == 31) f = s | 0x7f800000u | (m << 13);
    else f = s | ((e + 112u) << 23) | (m << 13);
    float out;
    memcpy(&out, &f, 4);
    return out;
}

static void UploadMaterials(UINT64 uvBytes, UINT64 colBytes);

static void BuildScene()
{
    double t0 = NowMs();
    std::vector<MeshDesc> meshes;
    std::vector<SubDesc> subs;
    std::vector<int> instMesh;
    std::vector<float> instMat;
    std::vector<int> instFlags;
    std::vector<int> varMesh, varFirst, varTiles, instVar;
    {
        std::lock_guard<std::mutex> l(gSceneMutex);
        meshes.swap(gMeshIn);
        subs.swap(gSubIn);
        instMesh.swap(gInstMeshIn);
        instMat.swap(gInstMatIn);
        instFlags.swap(gInstFlagsIn);
        varMesh.swap(gVarMeshIn);
        varFirst.swap(gVarFirstIn);
        varTiles.swap(gVarTilesIn);
        instVar.swap(gInstVarIn);
    }
    if (meshes.empty()) { Log("[TraceSixRT] no meshes handed over: nothing to build"); return; }
    ID3D11Resource* any = nullptr;
    for (const MeshDesc& md : meshes) if (md.vb) { any = (ID3D11Resource*)md.vb; break; }
    if (!EnsureDevices(any)) return;
    if (!WaitIdle()) return;
    DropScene();
    const double mb = 1.0 / 1048576.0;
    double vramBefore = 0.0, vramBudget = 0.0, vramSys = 0.0;
    bool vramRead = VramNow(vramBefore, vramBudget, vramSys);

    //--- 1. where each mesh goes in one position buffer (float3) and one index buffer (uint32) -----------
    // (0.24.150) Less video memory, nothing left out: a mesh whose own indices are 16-bit keeps them 16-bit (its region in
    // gIdx 4-byte aligned, the row's flag 4); texture coordinates are stored only for meshes with a material tile (in their
    // first use or a variant), colours only for those of them that have a colour stream - the trace reads neither for the rest.
    const size_t n = meshes.size();
    std::vector<UINT64> vBase(n, 0), iBase(n, 0), iCount(n, 0), iByte(n, 0), iLenB(n, 0), uBase(n, 0), cBase(n, 0);
    std::vector<UINT> vBytes(n, 0), iBytes(n, 0), uBytes(n, 0), cBytes(n, 0);   // (uBytes / cBytes: a texture coordinate / colour stream of its own, else 0)
    std::vector<char> ok(n, 0), idx16(n, 0), needUV(n, 0), needCol(n, 0), hasTile(n, 0);
    UINT64 totalV = 0, totalI = 0, totalIB = 0, totalU = 0, totalC = 0;
    int skipped = 0;
    for (size_t m = 0; m < n; m++)
        for (int s = 0; s < meshes[m].subCount; s++)
            if (meshes[m].firstSub + s < (int)subs.size() && subs[meshes[m].firstSub + s].tile >= 0) { hasTile[m] = 1; break; }
    for (size_t v = 0; v < varMesh.size() && v < varFirst.size(); v++)
    {
        int m = varMesh[v];
        if (m < 0 || m >= (int)n) continue;
        for (int s = 0; s < meshes[m].subCount; s++)
        {
            size_t at = (size_t)varFirst[v] + (size_t)s;
            if (at < varTiles.size() && varTiles[at] >= 0) { hasTile[m] = 1; break; }
        }
    }
    for (size_t m = 0; m < n; m++)
    {
        MeshDesc& md = meshes[m];
        if (!md.vb || !md.ib || md.vertexCount <= 0 || md.stride <= 0) { skipped++; continue; }
        D3D11_BUFFER_DESC vd = {}, id = {};
        ((ID3D11Buffer*)md.vb)->GetDesc(&vd);
        ((ID3D11Buffer*)md.ib)->GetDesc(&id);
        UINT64 posSize = md.posFormat == 0 ? 12 : 6;
        if ((UINT64)md.posOffset + ((UINT64)md.vertexCount - 1) * (UINT64)md.stride + posSize > vd.ByteWidth) { skipped++; continue; }
        // its texture coordinates, where they can be read (else none: the voxels' colour)
        if (md.uvFormat > 0 && md.uvVb && md.uvStride > 0)
        {
            D3D11_BUFFER_DESC ud = vd;
            if (md.uvVb != md.vb) ((ID3D11Buffer*)md.uvVb)->GetDesc(&ud);
            UINT64 uvSize = md.uvFormat == 1 ? 8 : 4;
            if ((UINT64)md.uvOffset + ((UINT64)md.vertexCount - 1) * (UINT64)md.uvStride + uvSize > ud.ByteWidth) md.uvFormat = 0;
            else if (md.uvVb != md.vb) uBytes[m] = ud.ByteWidth;
        }
        else md.uvFormat = 0;
        // its colours (the painted materials' layer weights), where they can be read
        if (md.colFormat > 0 && md.colVb && md.colStride > 0)
        {
            D3D11_BUFFER_DESC cd = vd;
            if (md.colVb != md.vb) ((ID3D11Buffer*)md.colVb)->GetDesc(&cd);
            UINT64 colSize = md.colFormat == 1 ? 4 : md.colFormat == 2 ? 16 : 8;
            if ((UINT64)md.colOffset + ((UINT64)md.vertexCount - 1) * (UINT64)md.colStride + colSize > cd.ByteWidth) md.colFormat = 0;
            else if (md.colVb != md.vb && md.colVb != md.uvVb) cBytes[m] = cd.ByteWidth;
            else if (md.colVb != md.vb && md.colVb == md.uvVb && uBytes[m] == 0) md.colFormat = 0;   // (the texture coordinates' stream, not staged: none)
        }
        else md.colFormat = 0;
        vBytes[m] = vd.ByteWidth;
        iBytes[m] = id.ByteWidth;
        iCount[m] = md.index32 ? id.ByteWidth / 4 : id.ByteWidth / 2;
        vBase[m] = totalV;
        iBase[m] = totalI;
        totalV += (UINT64)md.vertexCount;
        totalI += iCount[m];
        idx16[m] = md.index32 ? 0 : 1;
        iByte[m] = totalIB;
        iLenB[m] = ((idx16[m] ? iCount[m] * 2 : iCount[m] * 4) + 3) & ~3ull;
        totalIB += iLenB[m];
        needUV[m] = md.uvFormat > 0 && hasTile[m] ? 1 : 0;
        needCol[m] = md.colFormat > 0 && needUV[m] ? 1 : 0;
        uBase[m] = totalU;
        cBase[m] = totalC;
        if (needUV[m]) totalU += (UINT64)md.vertexCount;
        if (needCol[m]) totalC += (UINT64)md.vertexCount;
        ok[m] = 1;
    }
    // (gIdx padded: TriIdx reads 8 bytes from the 4-byte boundary at or before a 16-bit triangle)
    UINT64 posBytes = totalV * 12, idxBytes = totalIB + 16, uvBytes = totalU * 8, colBytes = totalC * 4;
    gPos = MakeBuffer(posBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
    gIdx = MakeBuffer(idxBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
    gUV = MakeBuffer(uvBytes > 0 ? uvBytes : 8, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
    gCol = MakeBuffer(colBytes > 0 ? colBytes : 4, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
    if (!gPos || !gIdx || !gUV || !gCol) { Log("[TraceSixRT] geometry buffers failed (%.0f MB + %.0f MB + %.0f MB)", posBytes * mb, idxBytes * mb, uvBytes * mb); gStatus = RT_BUILD_FAILED; DropScene(); return; }

    //--- 2. the game's buffers read back a batch at a time, converted and sent on ------------------------
    // D3D11: a batch of meshes' vertex + index buffers into one staging buffer, one Map. Converted from there
    // straight into a D3D12 upload buffer (positions to float3, indices to uint32: at most twice the size)
    // and copied into place. Nothing of the whole map is held in system memory at once.
    const UINT kStage = 128u << 20;
    const UINT64 kUp = 256ull << 20;
    ComPtr<ID3D11Buffer> stage;
    {
        D3D11_BUFFER_DESC bd = {};
        bd.ByteWidth = kStage;
        bd.Usage = D3D11_USAGE_STAGING;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(g11->CreateBuffer(&bd, nullptr, &stage))) { Log("[TraceSixRT] staging buffer failed"); gStatus = RT_BUILD_FAILED; DropScene(); return; }
    }
    ComPtr<ID3D12Resource> up = MakeBuffer(kUp, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
    BYTE* upMem = nullptr;
    if (!up || FAILED(up->Map(0, nullptr, (void**)&upMem))) { gStatus = RT_BUILD_FAILED; DropScene(); return; }
    UINT64 bytesRead = 0;
    int batches = 0;
    double tWait = 0.0;
    size_t next = 0;
    while (next < n)
    {
        struct Slot { size_t mesh; UINT vOff, iOff, uOff, cOff; };
        std::vector<Slot> slots;
        UINT used = 0;
        UINT64 vFirst = 0, vEnd = 0, iFirst = 0, iEnd = 0;
        UINT64 ibFirst = 0, ibEnd = 0, uFirst = 0, uEnd = 0, cFirst = 0, cEnd = 0;   // (0.24.150) the compacted ranges
        size_t m = next;
        for (; m < n; m++)
        {
            if (!ok[m]) continue;
            UINT vAl = (vBytes[m] + 15) & ~15u, iAl = (iBytes[m] + 15) & ~15u, uAl = (uBytes[m] + 15) & ~15u, cAl = (cBytes[m] + 15) & ~15u;
            UINT64 conv = (UINT64)meshes[m].vertexCount * 24 + iCount[m] * 4;
            if ((UINT64)vAl + iAl + uAl + cAl > kStage || conv > kUp) { ok[m] = 0; skipped++; continue; }   // (no mesh is this big)
            if (slots.empty()) { vFirst = vBase[m]; iFirst = iBase[m]; ibFirst = iByte[m]; uFirst = uBase[m]; cFirst = cBase[m]; }
            UINT64 vNext = vBase[m] + meshes[m].vertexCount, iNext = iBase[m] + iCount[m];
            if (!slots.empty() && (used + vAl + iAl + uAl + cAl > kStage || (vNext - vFirst) * 24 + (iNext - iFirst) * 4 > kUp)) break;
            Slot s = { m, used, used + vAl, used + vAl + iAl, used + vAl + iAl + uAl };
            D3D11_BOX vb = { 0, 0, 0, vBytes[m], 1, 1 }, ib = { 0, 0, 0, iBytes[m], 1, 1 };
            gCtx11->CopySubresourceRegion(stage.Get(), 0, s.vOff, 0, 0, (ID3D11Buffer*)meshes[m].vb, 0, &vb);
            gCtx11->CopySubresourceRegion(stage.Get(), 0, s.iOff, 0, 0, (ID3D11Buffer*)meshes[m].ib, 0, &ib);
            if (uBytes[m] > 0)
            {
                D3D11_BOX ub = { 0, 0, 0, uBytes[m], 1, 1 };
                gCtx11->CopySubresourceRegion(stage.Get(), 0, s.uOff, 0, 0, (ID3D11Buffer*)meshes[m].uvVb, 0, &ub);
            }
            if (cBytes[m] > 0)
            {
                D3D11_BOX cb = { 0, 0, 0, cBytes[m], 1, 1 };
                gCtx11->CopySubresourceRegion(stage.Get(), 0, s.cOff, 0, 0, (ID3D11Buffer*)meshes[m].colVb, 0, &cb);
            }
            used += vAl + iAl + uAl + cAl;
            vEnd = vNext;
            iEnd = iNext;
            ibEnd = iByte[m] + iLenB[m];
            uEnd = uBase[m] + (needUV[m] ? (UINT64)meshes[m].vertexCount : 0);
            cEnd = cBase[m] + (needCol[m] ? (UINT64)meshes[m].vertexCount : 0);
            slots.push_back(s);
        }
        next = m;
        if (slots.empty()) continue;
        batches++;
        double w0 = NowMs();
        D3D11_MAPPED_SUBRESOURCE ms = {};
        if (FAILED(gCtx11->Map(stage.Get(), 0, D3D11_MAP_READ, 0, &ms))) { Log("[TraceSixRT] staging map failed"); gStatus = RT_BUILD_FAILED; DropScene(); return; }
        tWait += NowMs() - w0;
        const BYTE* base = (const BYTE*)ms.pData;
        // (0.24.150) in the upload buffer: positions, indices (compacted bytes), texture coordinates, colours (compacted) -
        // within the bound checked above (24 bytes a vertex, 4 an index), which the compacted sizes never exceed
        const UINT64 posLen = (vEnd - vFirst) * 12, idxLen = ibEnd - ibFirst, uvLen = (uEnd - uFirst) * 8, colLen = (cEnd - cFirst) * 4;
        float* posOut = (float*)upMem;
        BYTE* idxOut = upMem + posLen;
        float* uvOut = (float*)(upMem + posLen + idxLen);
        UINT32* colOut = (UINT32*)(upMem + posLen + idxLen + uvLen);
        for (const Slot& s : slots)
        {
            const MeshDesc& md = meshes[s.mesh];
            float* p = posOut + (vBase[s.mesh] - vFirst) * 3;
            const BYTE* v = base + s.vOff + md.posOffset;
            for (int i = 0; i < md.vertexCount; i++, v += md.stride, p += 3)
            {
                if (md.posFormat == 0) memcpy(p, v, 12);
                else
                {
                    UINT16 hv[3];
                    memcpy(hv, v, 6);
                    p[0] = Half(hv[0]); p[1] = Half(hv[1]); p[2] = Half(hv[2]);
                }
            }
            // texture coordinates (0, 0 without them)
            float* u = uvOut + (uBase[s.mesh] - uFirst) * 2;   // (0.24.150: only meshes that need them)
            const BYTE* uv = base + (uBytes[s.mesh] > 0 ? s.uOff : s.vOff) + md.uvOffset;
            int us = md.uvStride > 0 ? md.uvStride : md.stride;
            for (int i = 0; needUV[s.mesh] && i < md.vertexCount; i++, uv += us, u += 2)
            {
                if (md.uvFormat == 1) memcpy(u, uv, 8);
                else if (md.uvFormat == 2) { UINT16 hv[2]; memcpy(hv, uv, 4); u[0] = Half(hv[0]); u[1] = Half(hv[1]); }
                else if (md.uvFormat == 3) { UINT16 hv[2]; memcpy(hv, uv, 4); u[0] = hv[0] / 65535.0f; u[1] = hv[1] / 65535.0f; }
                else { u[0] = 0.0f; u[1] = 0.0f; }
            }
            // colours, as RGBA8 (white without them)
            UINT32* cp = colOut + (cBase[s.mesh] - cFirst);   // (0.24.150: only meshes that need them)
            const BYTE* cv = base + (cBytes[s.mesh] > 0 ? s.cOff : md.colVb == md.vb ? s.vOff : s.uOff) + md.colOffset;
            auto pack = [](float r, float g, float b, float a) -> UINT32
            {
                auto q = [](float x) -> UINT32 { x = x < 0.0f ? 0.0f : x > 1.0f ? 1.0f : x; return (UINT32)(x * 255.0f + 0.5f); };
                return q(r) | (q(g) << 8) | (q(b) << 16) | (q(a) << 24);
            };
            for (int i = 0; needCol[s.mesh] && i < md.vertexCount; i++, cv += md.colStride, cp++)
            {
                if (md.colFormat == 1) memcpy(cp, cv, 4);
                else if (md.colFormat == 2) { float f4[4]; memcpy(f4, cv, 16); *cp = pack(f4[0], f4[1], f4[2], f4[3]); }
                else if (md.colFormat == 3) { UINT16 h4[4]; memcpy(h4, cv, 8); *cp = pack(Half(h4[0]), Half(h4[1]), Half(h4[2]), Half(h4[3])); }
                else if (md.colFormat == 4) { UINT16 h4[4]; memcpy(h4, cv, 8); *cp = pack(h4[0] / 65535.0f, h4[1] / 65535.0f, h4[2] / 65535.0f, h4[3] / 65535.0f); }
                else *cp = 0xffffffffu;
            }
            // indices as the mesh has them (0.24.150: 16-bit ones stay 16-bit), the region padded to 4 bytes with zeros
            BYTE* q = idxOut + (iByte[s.mesh] - ibFirst);
            const BYTE* ip = base + s.iOff;
            size_t iLen = (size_t)iCount[s.mesh] * (idx16[s.mesh] ? 2 : 4);
            memcpy(q, ip, iLen);
            if ((size_t)iLenB[s.mesh] > iLen) memset(q + iLen, 0, (size_t)iLenB[s.mesh] - iLen);
            bytesRead += vBytes[s.mesh] + iBytes[s.mesh];
        }
        gCtx11->Unmap(stage.Get(), 0);
        BeginBuildList();
        gBuildList->CopyBufferRegion(gPos.Get(), vFirst * 12, up.Get(), 0, (vEnd - vFirst) * 12);
        if (idxLen > 0) gBuildList->CopyBufferRegion(gIdx.Get(), ibFirst, up.Get(), posLen, idxLen);
        if (uvLen > 0) gBuildList->CopyBufferRegion(gUV.Get(), uFirst * 8, up.Get(), posLen + idxLen, uvLen);
        if (colLen > 0) gBuildList->CopyBufferRegion(gCol.Get(), cFirst * 4, up.Get(), posLen + idxLen + uvLen, colLen);
        if (!SubmitBuildListAndWait()) return;
    }
    up->Unmap(0, nullptr);
    up.Reset();
    stage.Reset();
    {
        BeginBuildList();
        D3D12_RESOURCE_BARRIER b[4];
        Transition(gPos.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[0]);
        Transition(gIdx.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[1]);
        Transition(gUV.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[2]);
        Transition(gCol.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[3]);
        gBuildList->ResourceBarrier(4, b);
        if (!SubmitBuildListAndWait()) return;
    }
    double tRead = NowMs();
    double vGeo = 0.0, vBlas = 0.0, vTlas = 0.0, vIgnB = 0.0, vIgnS = 0.0;   // (0.24.149) video memory after each stage
    if (vramRead) VramNow(vGeo, vIgnB, vIgnS);

    //--- 3. a bottom-level structure per mesh (a geometry per solid submesh) ------------------------------
    std::vector<std::vector<D3D12_RAYTRACING_GEOMETRY_DESC>> geos(n);
    std::vector<UINT64> preSize(n, 0);
    UINT64 scratchMax = 0, tris = 0;
    D3D12_GPU_VIRTUAL_ADDRESS posVA = gPos->GetGPUVirtualAddress(), idxVA = gIdx->GetGPUVirtualAddress();
    const D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS blasFlags =
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_COMPACTION;
    // (MINIMIZE_MEMORY tried in 0.24.150: 749 -> 743 MB on his Streets with an AMD driver - not worth any trace cost, removed)
    int built = 0, cutouts = 0;
    // The geometry table: one row per geometry, in BLAS order (first index, first vertex, material tile, flags). An
    // instance's InstanceID is its mesh's first row, so a hit's row is InstanceID + GeometryIndex.
    // (0.24.150) 8 values a row: the first index is a byte offset into gIdx, flag 4 = 16-bit indices; then the first texture
    // coordinate and the first colour (0xffffffff: none stored), and two unused.
    const UINT32 kNone = 0xffffffffu;
    std::vector<UINT32> rows;
    std::vector<UINT32> firstRow(n, 0);
    std::vector<std::vector<int>> rowOfSub(n);   // (0.24.102) each listed submesh's row (-1: not built), for the variants
    std::vector<char> oneSided(n, 0);   // terrain: met from above only (see the instances)
    for (size_t m = 0; m < n; m++)
    {
        if (!ok[m]) continue;
        const MeshDesc& md = meshes[m];
        UINT64 meshTris = 0;
        firstRow[m] = (UINT32)(rows.size() / 8);
        rowOfSub[m].assign(md.subCount > 0 ? md.subCount : 0, -1);
        for (int s = 0; s < md.subCount; s++)
        {
            const SubDesc& sd = subs[md.firstSub + s];
            if (sd.indexCount < 3 || sd.baseVertex < 0 || sd.baseVertex >= md.vertexCount || sd.indexStart < 0
                || (UINT64)sd.indexStart + (UINT64)sd.indexCount > iCount[m]) continue;
            rowOfSub[m][s] = (int)(rows.size() / 8);
            D3D12_RAYTRACING_GEOMETRY_DESC g = {};
            g.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
            // alpha-tested: not opaque, so the trace sees each hit and lets it count by chance (Cover)
            // (0.24.117) see-through (alpha-tested, glass): each triangle reported once per ray (glass multiplies its colour in)
            g.Flags = (sd.flags & 1) ? D3D12_RAYTRACING_GEOMETRY_FLAG_NO_DUPLICATE_ANYHIT_INVOCATION : D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
            if (sd.flags & 1) cutouts++;
            if (sd.flags & 2) oneSided[m] = 1;
            const UINT64 isz = idx16[m] ? 2 : 4;
            rows.push_back((UINT32)(iByte[m] + (UINT64)sd.indexStart * isz));
            rows.push_back((UINT32)(vBase[m] + (UINT64)sd.baseVertex));
            rows.push_back(md.uvFormat > 0 && sd.tile >= 0 ? (UINT32)sd.tile : 0xffffffffu);   // (no texture coordinates: no tile)
            rows.push_back((UINT32)sd.flags | (idx16[m] ? 4u : 0u));
            rows.push_back(needUV[m] ? (UINT32)(uBase[m] + (UINT64)sd.baseVertex) : kNone);
            rows.push_back(needCol[m] ? (UINT32)(cBase[m] + (UINT64)sd.baseVertex) : kNone);
            rows.push_back(0u);
            rows.push_back(0u);
            g.Triangles.VertexBuffer.StartAddress = posVA + (vBase[m] + (UINT64)sd.baseVertex) * 12;
            g.Triangles.VertexBuffer.StrideInBytes = 12;
            g.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
            g.Triangles.VertexCount = (UINT)(md.vertexCount - sd.baseVertex);
            g.Triangles.IndexBuffer = idxVA + iByte[m] + (UINT64)sd.indexStart * isz;
            g.Triangles.IndexFormat = idx16[m] ? DXGI_FORMAT_R16_UINT : DXGI_FORMAT_R32_UINT;
            g.Triangles.IndexCount = (UINT)(sd.indexCount / 3 * 3);
            geos[m].push_back(g);
            meshTris += sd.indexCount / 3;
        }
        if (geos[m].empty()) continue;
        tris += meshTris;
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS in = {};
        in.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        in.Flags = blasFlags;
        in.NumDescs = (UINT)geos[m].size();
        in.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        in.pGeometryDescs = geos[m].data();
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO pre = {};
        g12->GetRaytracingAccelerationStructurePrebuildInfo(&in, &pre);
        preSize[m] = (pre.ResultDataMaxSizeInBytes + 255) & ~255ull;
        if (pre.ScratchDataSizeInBytes > scratchMax) scratchMax = pre.ScratchDataSizeInBytes;
        built++;
    }
    if (built == 0) { Log("[TraceSixRT] none of the %d meshes had triangles that could be read", (int)n); gStatus = RT_BUILD_FAILED; DropScene(); return; }
    // (0.24.102) the material variants' rows: their mesh's rows, each with the variant's own tile
    std::vector<UINT32> varRow(varMesh.size(), 0xffffffffu);
    for (size_t v = 0; v < varMesh.size(); v++)
    {
        int m = varMesh[v];
        if (m < 0 || m >= (int)n || geos[m].empty() || v >= varFirst.size()) continue;
        const MeshDesc& md = meshes[m];
        UINT32 start = (UINT32)(rows.size() / 8);
        bool any = false;
        for (int s = 0; s < md.subCount && s < (int)rowOfSub[m].size(); s++)
        {
            int r = rowOfSub[m][s];
            if (r < 0) continue;
            int t = varFirst[v] + s < (int)varTiles.size() ? varTiles[varFirst[v] + s] : -1;
            UINT32 rr[8];   // (copied first: push_back may move rows)
            for (int j = 0; j < 8; j++) rr[j] = rows[(size_t)r * 8 + j];
            rr[2] = md.uvFormat > 0 && t >= 0 ? (UINT32)t : 0xffffffffu;
            for (int j = 0; j < 8; j++) rows.push_back(rr[j]);
            any = true;
        }
        if (any) varRow[v] = start;
    }
    if (rows.size() / 8 >= (1u << 24)) { Log("[TraceSixRT] %d geometries: more than an instance id can name", (int)(rows.size() / 8)); gStatus = RT_BUILD_FAILED; DropScene(); return; }
    {
        UINT64 bytes = rows.size() * 4;
        gGeoBuf = MakeBuffer(bytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
        ComPtr<ID3D12Resource> up = MakeBuffer(bytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
        void* p = nullptr;
        if (!gGeoBuf || !up || FAILED(up->Map(0, nullptr, &p))) { gStatus = RT_BUILD_FAILED; DropScene(); return; }
        memcpy(p, rows.data(), (size_t)bytes);
        up->Unmap(0, nullptr);
        BeginBuildList();
        gBuildList->CopyBufferRegion(gGeoBuf.Get(), 0, up.Get(), 0, bytes);
        D3D12_RESOURCE_BARRIER b;
        Transition(gGeoBuf.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b);
        gBuildList->ResourceBarrier(1, &b);
        if (!SubmitBuildListAndWait()) return;
    }

    // Built in batches of up to kBatch bytes and compacted batch by batch into buffers of their own, so the
    // uncompacted structures of the whole map never sit in video memory at once. Within a batch several builds
    // run at once, each with its own slice of scratch (up to kScratch in all).
    //   (1.0.0) kBatch 512 -> 64 MB: a batch is ONE submission to the GPU, and one that runs past ~2 s is a driver reset
    // (TDR). 512 MB of structures is a fraction of a second on his card, not on a slower or memory-starved one.
    const UINT64 kBatch = 64ull << 20, kScratch = 256ull << 20;
    UINT64 slice = (scratchMax + 255) & ~255ull;
    int par = (int)(kScratch / slice);
    if (par < 1) par = 1;
    if (par > 16) par = 16;
    ComPtr<ID3D12Resource> scratch = MakeBuffer(slice * par, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    if (!scratch) { Log("[TraceSixRT] scratch memory failed (%.0f MB)", slice * par * mb); gStatus = RT_BUILD_FAILED; DropScene(); return; }
    std::vector<D3D12_GPU_VIRTUAL_ADDRESS> blasAddr(n, 0);
    UINT64 poolTotal = 0, compactTotal = 0, peakPool = 0;
    double tBuild = 0.0, tCompact = 0.0;
    size_t bStart = 0;
    while (bStart < n)
    {
        double b0 = NowMs();
        std::vector<size_t> batch;
        UINT64 poolSize = 0;
        size_t m = bStart;
        for (; m < n; m++)
        {
            if (geos[m].empty()) continue;
            if (!batch.empty() && poolSize + preSize[m] > kBatch) break;
            batch.push_back(m);
            poolSize += preSize[m];
        }
        bStart = m;
        if (batch.empty()) continue;
        int count = (int)batch.size();
        ComPtr<ID3D12Resource> pool = MakeBuffer(poolSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        ComPtr<ID3D12Resource> sizes = MakeBuffer(8ull * count, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        ComPtr<ID3D12Resource> sizesRead = MakeBuffer(8ull * count, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST);
        if (!pool || !sizes || !sizesRead) { Log("[TraceSixRT] memory for a batch of structures failed (%.0f MB)", poolSize * mb); gStatus = RT_BUILD_FAILED; DropScene(); return; }
        if (poolSize > peakPool) peakPool = poolSize;
        std::vector<UINT64> off(count, 0);
        BeginBuildList();
        UINT64 o = 0;
        for (int k = 0; k < count; k++)
        {
            size_t mi = batch[k];
            off[k] = o;
            o += preSize[mi];
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC bd = {};
            bd.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
            bd.Inputs.Flags = blasFlags;
            bd.Inputs.NumDescs = (UINT)geos[mi].size();
            bd.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
            bd.Inputs.pGeometryDescs = geos[mi].data();
            bd.DestAccelerationStructureData = pool->GetGPUVirtualAddress() + off[k];
            bd.ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress() + slice * (k % par);
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC pi = {};
            pi.InfoType = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_COMPACTED_SIZE;
            pi.DestBuffer = sizes->GetGPUVirtualAddress() + 8ull * k;
            gBuildList->BuildRaytracingAccelerationStructure(&bd, 1, &pi);
            if ((k + 1) % par == 0) UavBarrier(gBuildList.Get(), scratch.Get());   // before the slices are used again
            if ((k + 1) % 4096 == 0) { if (!SubmitBuildListAndWait()) return; BeginBuildList(); }
        }
        UavBarrier(gBuildList.Get(), pool.Get());
        UavBarrier(gBuildList.Get(), scratch.Get());
        {
            D3D12_RESOURCE_BARRIER b;
            Transition(sizes.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE, b);
            gBuildList->ResourceBarrier(1, &b);
            gBuildList->CopyBufferRegion(sizesRead.Get(), 0, sizes.Get(), 0, 8ull * count);
        }
        if (!SubmitBuildListAndWait()) return;
        double b1 = NowMs();
        tBuild += b1 - b0;

        // compaction: each structure copied into just the room it needs, into this batch's own buffer
        std::vector<UINT64> csize(count, 0);
        {
            UINT64* p = nullptr;
            D3D12_RANGE rr = { 0, (SIZE_T)(8ull * count) };
            if (FAILED(sizesRead->Map(0, &rr, (void**)&p))) { gStatus = RT_BUILD_FAILED; DropScene(); return; }
            memcpy(csize.data(), p, 8ull * count);
            D3D12_RANGE none = { 0, 0 };
            sizesRead->Unmap(0, &none);
        }
        UINT64 cTotal = 0;
        std::vector<UINT64> coff(count, 0);
        for (int k = 0; k < count; k++) { coff[k] = cTotal; cTotal += (csize[k] + 255) & ~255ull; }
        ComPtr<ID3D12Resource> dst = cTotal > 0 && cTotal < poolSize
            ? MakeBuffer(cTotal, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS)
            : nullptr;
        if (dst)
        {
            BeginBuildList();
            for (int k = 0; k < count; k++)
            {
                D3D12_GPU_VIRTUAL_ADDRESS to = dst->GetGPUVirtualAddress() + coff[k];
                gBuildList->CopyRaytracingAccelerationStructure(to, pool->GetGPUVirtualAddress() + off[k], D3D12_RAYTRACING_ACCELERATION_STRUCTURE_COPY_MODE_COMPACT);
                blasAddr[batch[k]] = to;
            }
            UavBarrier(gBuildList.Get(), dst.Get());
            if (!SubmitBuildListAndWait()) return;
            gBlasPools.push_back(dst);
            compactTotal += cTotal;
        }
        else
        {
            // (no room for the compacted copy, or nothing to gain: the batch stays as built)
            for (int k = 0; k < count; k++) blasAddr[batch[k]] = pool->GetGPUVirtualAddress() + off[k];
            gBlasPools.push_back(pool);
            compactTotal += poolSize;
        }
        poolTotal += poolSize;
        tCompact += NowMs() - b1;
    }
    scratch.Reset();
    if (vramRead) VramNow(vBlas, vIgnB, vIgnS);
    double tBlas = NowMs();

    //--- 4. the top level: one instance per placed mesh -------------------------------------------------
    std::vector<D3D12_RAYTRACING_INSTANCE_DESC> id;
    id.reserve(instMesh.size());
    gInstSlot.assign(instMesh.size(), -1);
    for (size_t i = 0; i < instMesh.size(); i++)
    {
        int m = instMesh[i];
        if (m < 0 || m >= (int)n || !blasAddr[m]) continue;
        gInstSlot[i] = (int)id.size();
        D3D12_RAYTRACING_INSTANCE_DESC d = {};
        memcpy(d.Transform, &instMat[12 * i], sizeof(d.Transform));
        d.InstanceID = firstRow[m];   // the trace finds a hit's triangle through the geometry table from here
        // (0.24.102) a copy placed with other materials: its variant's rows (same geometry, its own tiles)
        if (i < instVar.size() && instVar[i] >= 0 && instVar[i] < (int)varRow.size() && varRow[instVar[i]] != 0xffffffffu)
            d.InstanceID = varRow[instVar[i]];
        // A lamp's own fixture: mask 2, which lamp shadow rays leave out (a point light sits inside its bulb or
        // housing - a shadow ray to it would always be stopped by that); everything else, light and sun rays
        // included, sees every instance.
        // (0.24.98) flag 2 = it keeps the game's own sun shadow in RayTracedWorld (alpha-tested): mask 4, which the sun's
        // rays leave out in that mode.
        int fl = i < instFlags.size() ? instFlags[i] : 0;
        d.InstanceMask = (fl & 1) ? 0x02 : (fl & 2) ? 0x04 : 0x01;
        // Two-sided, as Tarkov's single-sided roofs and walls must stop light from behind too - except the terrain.
        // The game draws it with level-of-detail patches a few centimetres off the heightmap these triangles are
        // made from, varying with where the camera is: rays starting on the drawn ground can start under this
        // one and hit its underside at once (black patches that moved with the player). A ray can only reach a
        // heightfield's underside from below it, so the trace culls back faces and the terrain alone takes part
        // (its triangles are clockwise seen from above: front facing, in DXR's left-handed default).
        d.Flags = oneSided[m] ? D3D12_RAYTRACING_INSTANCE_FLAG_NONE : D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE;
        d.AccelerationStructure = blasAddr[m];
        id.push_back(d);
    }
    if (id.empty()) { Log("[TraceSixRT] no instances to place"); gStatus = RT_BUILD_FAILED; DropScene(); return; }
    gInstBuf = MakeBuffer(id.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC), D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
    gInstDef = MakeBuffer(id.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC), D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
    if (!gInstBuf || !gInstDef) { gStatus = RT_BUILD_FAILED; DropScene(); return; }
    {
        void* p = nullptr;
        gInstBuf->Map(0, nullptr, &p);
        memcpy(p, id.data(), id.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC));
        gInstBuf->Unmap(0, nullptr);
    }
    D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS tin = {};
    tin.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
    tin.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE;
    tin.NumDescs = (UINT)id.size();
    tin.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
    tin.InstanceDescs = gInstDef->GetGPUVirtualAddress();
    D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO tp = {};
    g12->GetRaytracingAccelerationStructurePrebuildInfo(&tin, &tp);
    gTlas = MakeBuffer(tp.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    gTlasUpd = MakeBuffer(tp.UpdateScratchDataSizeInBytes > 0 ? tp.UpdateScratchDataSizeInBytes : 256, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    gTlasIn = tin;
    ComPtr<ID3D12Resource> tscratch = MakeBuffer(tp.ScratchDataSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    if (!gTlas || !tscratch) { gStatus = RT_BUILD_FAILED; DropScene(); return; }
    BeginBuildList();
    gBuildList->CopyBufferRegion(gInstDef.Get(), 0, gInstBuf.Get(), 0, id.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC));
    {
        D3D12_RESOURCE_BARRIER ib;
        Transition(gInstDef.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, ib);
        gBuildList->ResourceBarrier(1, &ib);
    }
    D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC tb = {};
    tb.Inputs = tin;
    tb.DestAccelerationStructureData = gTlas->GetGPUVirtualAddress();
    tb.ScratchAccelerationStructureData = tscratch->GetGPUVirtualAddress();
    gBuildList->BuildRaytracingAccelerationStructure(&tb, 0, nullptr);
    UavBarrier(gBuildList.Get(), gTlas.Get());
    if (!SubmitBuildListAndWait()) return;
    double tTlas = NowMs();
    size_t instBytes = id.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC);
    gInstBuf.Reset();   // (in gInstDef now)
    tscratch.Reset();   // (the top level's build scratch: done with)
    if (vramRead) VramNow(vTlas, vIgnB, vIgnS);

    gStats[0] = (float)n;
    gStats[1] = (float)built;
    gStats[2] = (float)id.size();
    gStats[3] = (float)tris;
    gStats[4] = (float)(posBytes * mb);
    gStats[5] = (float)(idxBytes * mb);
    gStats[6] = (float)(poolTotal * mb);
    gStats[7] = (float)(compactTotal * mb);
    gStats[8] = (float)(tp.ResultDataMaxSizeInBytes * mb);
    gStats[9] = (float)(tTlas - t0);
    Name(gPos.Get(), L"TraceSix positions");
    Name(gIdx.Get(), L"TraceSix indices");
    Name(gTlas.Get(), L"TraceSix top level");
    Name(gInstBuf.Get(), L"TraceSix instances");
    Name(gGeoBuf.Get(), L"TraceSix geometry rows");
    Name(gUV.Get(), L"TraceSix texture coordinates");
    Name(gCol.Get(), L"TraceSix vertex colours");
    for (size_t i = 0; i < gBlasPools.size(); i++)
    {
        wchar_t nm[64];
        swprintf_s(nm, L"TraceSix bottom level batch %u", (unsigned)i);
        Name(gBlasPools[i].Get(), nm);
    }
    Log("[TraceSixRT] Scene built: %d meshes (%d with triangles, %d unreadable), %d placed, %.2f million triangles, %d of %d geometries alpha-tested. "
        "Video memory kept: positions %.0f MB + indices %.0f MB + bottom-level structures %.0f MB (%.0f MB before compaction, largest batch %.0f MB) + top level %.1f MB + instances %.1f MB. "
        "Render-thread time %.0f ms: read-back and upload %.0f ms (%.0f MB in %d batches, %.0f ms of it waiting on the game's GPU), bottom-level builds %.0f ms, compaction %.0f ms, top level %.0f ms.",
        (int)n, built, skipped, (int)id.size(), tris / 1e6, cutouts, (int)(rows.size() / 8),
        posBytes * mb, idxBytes * mb, compactTotal * mb, poolTotal * mb, peakPool * mb, tp.ResultDataMaxSizeInBytes * mb, id.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC) * mb,
        tTlas - t0, tRead - t0, bytesRead * mb, batches, tWait, tBuild, tCompact, tTlas - tBlas);
    (void)instBytes;
    UploadMaterials(uvBytes, colBytes);
    double vramAfter = 0.0, vramBudget2 = 0.0, vramSys2 = 0.0;
    if (vramRead && VramNow(vramAfter, vramBudget2, vramSys2))
        Log("[TraceSixRT] Video memory of this process: %.2f GB before the scene build, %.2f GB after it (%+.2f GB: geometry %+.2f, bottom-level structures %+.2f, top level %+.2f, materials %+.2f), of a %.2f GB budget (%.2f GB moved out to system memory).",
            vramBefore, vramAfter, vramAfter - vramBefore, vGeo - vramBefore, vBlas - vGeo, vTlas - vBlas, vramAfter - vTlas, vramBudget2, vramSys2);
    gSceneReady = 1;
    gStatus = RT_READY;
}

//--- the per-frame trace ------------------------------------------------------------------------------

static DXGI_FORMAT Typed(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R16_TYPELESS: return DXGI_FORMAT_R16_FLOAT;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    default: return f;
    }
}

// A D3D12 texture shared into D3D11, the size and format of a Unity texture (the FSR4 bridge's EnsureSharedTex:
// which resource flags D3D11 accepts on a shared texture differs by format and driver - tried in turn).
static bool EnsureShared(SharedTex& st, void* unityPtr, bool uav, const char* label)
{
    ID3D11Texture2D* unity = (ID3D11Texture2D*)unityPtr;
    D3D11_TEXTURE2D_DESC ud = {};
    unity->GetDesc(&ud);
    if (st.tex11 && st.unity == unityPtr && st.w == ud.Width && st.h == ud.Height) return true;
    st = SharedTex{};
    DXGI_FORMAT f = Typed(ud.Format);
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_FLAGS u = uav ? D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS : D3D12_RESOURCE_FLAG_NONE;
    D3D12_RESOURCE_FLAGS combos[] = {
        D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | u, u,
        D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | u | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS };
    std::string tried;
    for (D3D12_RESOURCE_FLAGS c : combos)
    {
        char why[160];
        D3D12_RESOURCE_DESC rd = {};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = ud.Width;
        rd.Height = ud.Height;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = f;
        rd.SampleDesc = {1, 0};
        rd.Flags = c;
        ComPtr<ID3D12Resource> r;
        HRESULT hr = g12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_SHARED, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&r));
        if (FAILED(hr)) { snprintf(why, sizeof(why), "; flags 0x%x: D3D12 CreateCommittedResource failed (0x%08lX)", (unsigned)c, (unsigned long)hr); tried += why; continue; }
        HANDLE h = nullptr;
        hr = g12->CreateSharedHandle(r.Get(), nullptr, GENERIC_ALL, nullptr, &h);
        if (FAILED(hr)) { snprintf(why, sizeof(why), "; flags 0x%x: CreateSharedHandle failed (0x%08lX)", (unsigned)c, (unsigned long)hr); tried += why; continue; }
        ComPtr<ID3D11Texture2D> t;
        hr = g11_1->OpenSharedResource1(h, IID_PPV_ARGS(&t));
        CloseHandle(h);
        if (FAILED(hr)) { snprintf(why, sizeof(why), "; flags 0x%x: D3D11 OpenSharedResource1 failed (0x%08lX)", (unsigned)c, (unsigned long)hr); tried += why; continue; }
        st.res12 = r; st.tex11 = t; st.unity = unityPtr; st.w = ud.Width; st.h = ud.Height; st.fmt = f;
        return true;
    }
    Log("[TraceSixRT] could not share the %s texture (%ux%u, format %d)%s", label, ud.Width, ud.Height, (int)f, tried.c_str());
    return false;
}

// Per-frame parameters, in slots: C# writes slot n on the main thread and the events for slot n read it on the
// render thread, which can be a frame behind (the FSR4 bridge's scheme).
// (0.24.76) A trace is two events: Issue (16 + slot: this frame's rays handed over, the trace started on the compute
// queue) and Collect (20 + slot: D3D11 waits for it, the results copied back). GiRtCamera sends Issue before the
// game's reflections and lighting ("Trace During Lighting") so the GPU traces while it lights the frame, and Collect
// where the light is needed; or both back to back, as before.
static const int kSlots = 4;
static const int kConst = 32;    // the shader's cbuffer: 8 float4
static const int kOut = 5;       // where the rays ended: the hit, the second bounce, the lamps, the cell read (gHit, gHitB, gHitC, gHitD, gHitE)
static const int kMaxLamps = 64; // GISix's lamp arrays (GiLamps.Max)
static const int kLampFloats = 16;   // per lamp: pos, dir, col, up
struct FrameParams { void* rayO; void* rayD; void* rayV; void* open; void* out[kOut]; float c[kConst]; float lamps[kMaxLamps * kLampFloats];
                     void* aoO; void* aoN; void* ao;      // (0.24.86, RtSetAo) the AO rays' starts and normals in, the AO out (null = none)
                     void* reflO; void* reflD; void* reflOut[4];      // (0.24.87, RtSetRefl) SSRSix's reflection rays in; hit, lamp, cell, colour out
                     void* alb; void* sunO; void* sunOut;             // (0.24.89, RtSetExtra) each light ray's hit colour out; the sun's rays in, its shadow out
                     void* lampO;                                     // (0.24.115, RtSetExtra) the picture's lamp shadow rays in (their ends: gSunOut yzw)
                     void* sunTint; };                                // (0.24.117, RtSetExtra) what coloured glass lets through toward the sun, out
static FrameParams gFrame[kSlots];
static std::mutex gFrameMutex;
// (0.24.120) "Ray-Traced Sun Shadows In Scopes": the scope camera's own sun rays (GiRtScope, RtSetScope) - CSSun again, from
// the scope's ray starts into its own shadow (and glass colour), on a command list of its own with a descriptor table of its
// own (the heap's second half: the sun's views the scope's, the atlases the same, the rest empty). Started at the scope's
// BeforeLighting (28 + slot), collected before its sun's shadow mask (32 + slot). Its constants are C#'s, whole.
// (0.24.128) an extra set's frame: its rays in (o: starts; d: directions, reflection sets), its outputs (a sun set: shadow, glass
// colour; a reflection set: hit, lamp, cell, colour), the constants
struct ExtraParams { void* o; void* d; void* out[4]; float c[kConst]; };

static SharedTex gRayOTex, gRayDTex, gOpenTex;   // the rays in (GISix's RTRAYS pass makes them); which cells hold light (CELLOPEN)
static SharedTex gRayVTex;                       // the reuse rays' ends (RTRAYS, "Reuse Ray Samples")
static void* gRayVUnity = nullptr;
static SharedTex gOutTex[kOut];                  // where they ended out
// (0.24.86) "Ambient Occlusion" = Ray Traced: the AO rays (GISix's RTAORAYS pass, one per picture pixel) in, the AO out.
// A texture that will not share turns the AO off for the session, not the ray tracing.
static SharedTex gAoOTex, gAoNTex, gAoTex;
static bool gAoFailed = false;
static void* gPendingAo[kSlots] = {};
static bool gAoRan[3] = {};
static double gAoSum = 0.0;
static int gAoN = 0;
// (0.24.87) SSRSix's off-screen reflection rays (one mirror ray per traced pixel) in, their ends out - the hit, the lamp,
// the cell - for GISixProbes RTREFLSHADE to light. A texture that will not share turns them off for the session.
static const int kReflOut = 4;
static SharedTex gReflOTex, gReflDTex, gReflOutTex[kReflOut];
static const int kTable = 16 + kOut + kReflOut;   // (0.24.120) descriptors in one table (the root signature's ranges); the heap holds two
static bool gReflFailed = false;
static void* gPendingRefl[kSlots][kReflOut] = {};
static bool gReflRan[3] = {};
static double gReflSum = 0.0;
static int gReflN = 0;
// (0.24.89) Each light ray's first hit's colour (CSMain); the sun's shadow (CSSun: rays in, shadow out) - on a command list
// of its own, signalled before the rest (gPendingSun), collected by event 24 + slot from the game's lighting.
static SharedTex gAlbTex, gSunOTex, gSunOutTex;
static SharedTex gLampOTex;          // (0.24.115) the picture's lamp shadow rays (CSSun's LampDirect, c[29] bit 2)
static bool gLampFailed = false;
static SharedTex gSunTintTex;        // (0.24.117) the sun rays' glass colour (CSSun's GlassThrough, c[29] bit 4)
static bool gTintFailed = false;
static void* gPendingSunTint[kSlots] = {};
static bool gSunFailed = false, gAlbFailed = false;
static void* gPendingAlb[kSlots] = {};
static UINT64 gPendingSun[kSlots] = {};
static void* gPendingSunOut[kSlots] = {};
static bool gSunRan[3] = {};
static double gSunSum = 0.0;
static int gSunN = 0;
static void* gOpenUnity = nullptr;
// A started trace not yet collected: the fence value its end signals, and where its results go (per slot), and the
// last one started (Issue makes D3D11 wait for it before handing over another: the fence must only ever rise).
static UINT64 gPending[kSlots] = {};
static void* gPendingOut[kSlots][kOut] = {};
static UINT64 gUncollected = 0;

static ComPtr<ID3D12RootSignature> gRoot;
static ComPtr<ID3D12PipelineState> gPso;
static ComPtr<ID3D12PipelineState> gPsoAo;   // CSAo, on the same root signature (null: it failed to build - no AO)
static ComPtr<ID3D12PipelineState> gPsoRefl; // CSRefl, the same (null: no reflection rays)
static ComPtr<ID3D12PipelineState> gPsoSun;  // CSSun, the same (null: no sun shadows)
static ComPtr<ID3D12DescriptorHeap> gHeap;
static UINT gHeapStep = 0;
static const int kRing = 3;
static ComPtr<ID3D12CommandAllocator> gAlloc[kRing];   // (compute: the trace runs on gCompute)
static ComPtr<ID3D12Resource> gLampBuf[kRing];   // the frame's lamps (upload heap, one per ring slot: the GPU may still read the last one)
static UINT64 gAllocFence[kRing] = {};
static bool gAllocUsed[kRing] = {};
static ComPtr<ID3D12GraphicsCommandList4> gList;
static ComPtr<ID3D12CommandAllocator> gAllocSun[kRing];   // (0.24.89) the sun's command list, run and signalled first
static ComPtr<ID3D12GraphicsCommandList4> gListSun;
// (0.24.125) The extra ray sets (0.24.120's scope set, generalised): a kernel again, on a list, a ring and a descriptor table of
// each set's own (the heap's table 1 + set), from rays C# draws and hands over. Sun sets run CSSun (RtSetExtraSun: 0 = the
// scope's sun shadow, GiRtScope, events 28 / 32 + slot; 1 = the main picture's glass, GiGlassSun, 44 / 48; 2 = the scope's
// glass, 52 / 56); (0.24.128) reflection sets run CSRefl (RtSetExtraRefl: 3 = the main picture's glass reflections, 60 / 64).
// No set touches another's textures, pending collects or timings.
static const int kExtraSets = 4;
static const bool kXRefl[kExtraSets] = { false, false, false, true };
struct ExtraSun
{
    ExtraParams p[kSlots];
    SharedTex o, d, out[4];
    bool failed = false;
    int atlasGen = -1;
    UINT64 pending[kSlots] = {};
    void* pendingOut[kSlots][4] = {};
    double sum = 0.0;
    int n = 0;
    ComPtr<ID3D12CommandAllocator> alloc[kRing];
    UINT64 allocFence[kRing] = {};
    bool allocUsed[kRing] = {};
    ComPtr<ID3D12GraphicsCommandList4> list;
    ComPtr<ID3D12QueryHeap> queries;   // before and after its dispatch, per ring slot
    ComPtr<ID3D12Resource> queryRead;
    UINT64 no = 0;
    bool rode[kSlots] = {};   // (0.24.136) its rays went with the main trace's hand-over: Collect collects them
};
static ExtraSun gX[kExtraSets];
// (0.24.136) RtSetExtraRide: a set's rays for a slot ride the main trace's hand-over (Issue / Collect) instead of their own
// events - GiGlassSun's, when GiRtCamera placed them in its own buffer. Each separate hand-over cost ~0.15 ms of frame time
// (his SixBench run: the glass's sun rays, 0.013 ms of GPU work, cost 0.16 ms), the glass made two a frame.
static int gXRide[kExtraSets][kSlots] = {};
// a set readied for an issue (ExtraPrepare), what ExtraRun records from
struct ExtraGo { ExtraParams f; bool refl; ID3D12PipelineState* pso; void* want[4]; };
static bool ExtraPrepare(int x, int slot, ExtraGo& g);
static void ExtraCopyIn(int x, const ExtraGo& g);
static int ExtraRun(int x, const ExtraGo& g);
static void ExtraDone(int x, int slot, int ring, UINT64 done, const ExtraGo& g);
static void ExtraCollect(int x, int slot);
static const char* kXName[kExtraSets] = { "sun shadows in the scope", "sun shadows on the glass", "sun shadows on the glass in the scope",
                                          "reflections on the glass" };
static ComPtr<ID3D12QueryHeap> gQueries;
static ComPtr<ID3D12Resource> gQueryRead;
static UINT64 gTsFreq = 0;
static UINT64 gFrameNo = 0;
static double gTraceSum = 0.0, gTraceMax = 0.0, gCpuSum = 0.0;
static int gTraceN = 0, gCpuN = 0;

static void DropTextures()
{
    gRayOTex = SharedTex{}; gRayDTex = SharedTex{}; gOpenTex = SharedTex{}; gRayVTex = SharedTex{};
    gAoOTex = SharedTex{}; gAoNTex = SharedTex{}; gAoTex = SharedTex{};
    for (int s = 0; s < kSlots; s++) gPendingAo[s] = nullptr;
    gReflOTex = SharedTex{}; gReflDTex = SharedTex{};
    for (int i = 0; i < kReflOut; i++) gReflOutTex[i] = SharedTex{};
    for (int s = 0; s < kSlots; s++) for (int i = 0; i < kReflOut; i++) gPendingRefl[s][i] = nullptr;
    gAlbTex = SharedTex{}; gSunOTex = SharedTex{}; gSunOutTex = SharedTex{}; gLampOTex = SharedTex{}; gSunTintTex = SharedTex{};
    for (int x = 0; x < kExtraSets; x++)
    {
        gX[x].o = SharedTex{}; gX[x].d = SharedTex{}; gX[x].atlasGen = -1;
        for (int i = 0; i < 4; i++) gX[x].out[i] = SharedTex{};
        for (int s = 0; s < kSlots; s++) { gX[x].pending[s] = 0; for (int i = 0; i < 4; i++) gX[x].pendingOut[s][i] = nullptr; }
    }
    for (int s = 0; s < kSlots; s++) gPendingSunTint[s] = nullptr;
    for (int s = 0; s < kSlots; s++) { gPendingAlb[s] = nullptr; gPendingSun[s] = 0; gPendingSunOut[s] = nullptr; }
    gRayVUnity = nullptr;
    for (int i = 0; i < kOut; i++) gOutTex[i] = SharedTex{};
    gOpenUnity = nullptr;
    for (int s = 0; s < kSlots; s++) gPending[s] = 0;
}

static void DropPipeline()
{
    gRoot.Reset(); gPso.Reset(); gPsoAo.Reset(); gPsoRefl.Reset(); gPsoSun.Reset(); gHeap.Reset(); gList.Reset(); gListSun.Reset(); gQueries.Reset(); gQueryRead.Reset();
    for (int i = 0; i < kRing; i++) { gAlloc[i].Reset(); gAllocSun[i].Reset(); gLampBuf[i].Reset(); gAllocUsed[i] = false; gAllocFence[i] = 0; }
    for (int x = 0; x < kExtraSets; x++)
    {
        for (int i = 0; i < kRing; i++) { gX[x].alloc[i].Reset(); gX[x].allocUsed[i] = false; gX[x].allocFence[i] = 0; }
        gX[x].list.Reset(); gX[x].queries.Reset(); gX[x].queryRead.Reset();
    }
    gBuildAlloc.Reset(); gBuildList.Reset();
    DropTextures();
}

// Root: t0 the scene, a table (t1 rays' starts, t2 their directions, u0-u4 where they ended, t7 which cells hold
// light, t8 the reuse rays' ends, t9 / t10 the AO rays' starts and normals, u5 the AO, t11 / t12 the reflection rays,
// u6-u9 their ends, u10 the light rays' hit colours, t14 / t15 the material atlases, t17 the sun's rays, u11 its shadow,
// t19 the picture's lamp shadow rays (0.24.115), u12 the sun rays' glass colour (0.24.117)),
// t3 the geometry table, t4 the indices, t5 the positions, t13 the tile table, t16 the texture coordinates (buffers as
// root views; t18 the vertex colours too), t6 the lamps, b0 the constants, s0 a bilinear clamped sampler. CSMain, CSAo, CSRefl
// and CSSun share it.
static bool EnsurePipeline()
{
    if (gPso) return true;
    D3D12_DESCRIPTOR_RANGE r[13] = {};
    r[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r[0].NumDescriptors = 2; r[0].BaseShaderRegister = 1;
    r[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; r[1].NumDescriptors = kOut; r[1].BaseShaderRegister = 0;
    r[1].OffsetInDescriptorsFromTableStart = 2;
    r[2].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r[2].NumDescriptors = 2; r[2].BaseShaderRegister = 7;
    r[2].OffsetInDescriptorsFromTableStart = 2 + kOut;
    r[3].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r[3].NumDescriptors = 2; r[3].BaseShaderRegister = 9;
    r[3].OffsetInDescriptorsFromTableStart = 4 + kOut;
    r[4].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; r[4].NumDescriptors = 1; r[4].BaseShaderRegister = 5;
    r[4].OffsetInDescriptorsFromTableStart = 6 + kOut;
    r[5].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r[5].NumDescriptors = 2; r[5].BaseShaderRegister = 11;
    r[5].OffsetInDescriptorsFromTableStart = 7 + kOut;
    r[6].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; r[6].NumDescriptors = kReflOut; r[6].BaseShaderRegister = 6;
    r[6].OffsetInDescriptorsFromTableStart = 9 + kOut;
    r[7].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; r[7].NumDescriptors = 1; r[7].BaseShaderRegister = 10;
    r[7].OffsetInDescriptorsFromTableStart = 9 + kOut + kReflOut;
    r[8].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r[8].NumDescriptors = 2; r[8].BaseShaderRegister = 14;
    r[8].OffsetInDescriptorsFromTableStart = 10 + kOut + kReflOut;
    r[9].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r[9].NumDescriptors = 1; r[9].BaseShaderRegister = 17;
    r[9].OffsetInDescriptorsFromTableStart = 12 + kOut + kReflOut;
    r[10].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; r[10].NumDescriptors = 1; r[10].BaseShaderRegister = 11;
    r[10].OffsetInDescriptorsFromTableStart = 13 + kOut + kReflOut;
    r[11].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r[11].NumDescriptors = 1; r[11].BaseShaderRegister = 19;
    r[11].OffsetInDescriptorsFromTableStart = 14 + kOut + kReflOut;
    r[12].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; r[12].NumDescriptors = 1; r[12].BaseShaderRegister = 12;
    r[12].OffsetInDescriptorsFromTableStart = 15 + kOut + kReflOut;
    D3D12_ROOT_PARAMETER p[10] = {};
    p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[0].Descriptor.ShaderRegister = 0;
    p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; p[1].DescriptorTable.NumDescriptorRanges = 13; p[1].DescriptorTable.pDescriptorRanges = r;
    p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[2].Descriptor.ShaderRegister = 3;
    p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[3].Descriptor.ShaderRegister = 4;
    p[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[4].Descriptor.ShaderRegister = 5;
    p[5].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[5].Descriptor.ShaderRegister = 6;
    p[6].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; p[6].Constants.ShaderRegister = 0; p[6].Constants.Num32BitValues = kConst;
    p[7].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[7].Descriptor.ShaderRegister = 13;
    p[8].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[8].Descriptor.ShaderRegister = 16;
    p[9].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[9].Descriptor.ShaderRegister = 18;
    D3D12_STATIC_SAMPLER_DESC ss = {};
    ss.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
    ss.AddressU = ss.AddressV = ss.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    ss.MaxLOD = D3D12_FLOAT32_MAX;
    ss.ShaderRegister = 0;
    ss.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    D3D12_ROOT_SIGNATURE_DESC rs = {};
    rs.NumParameters = 10; rs.pParameters = p;
    rs.NumStaticSamplers = 1; rs.pStaticSamplers = &ss;
    ComPtr<ID3DBlob> blob, err;
    if (FAILED(D3D12SerializeRootSignature(&rs, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err)) ||
        FAILED(g12->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&gRoot))))
    {
        Log("[TraceSixRT] root signature failed%s%s", err ? ": " : "", err ? (const char*)err->GetBufferPointer() : "");
        return false;
    }
    D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
    pd.pRootSignature = gRoot.Get();
    pd.CS = { g_GISixRTTrace, sizeof(g_GISixRTTrace) };
    HRESULT hr = g12->CreateComputePipelineState(&pd, IID_PPV_ARGS(&gPso));
    if (FAILED(hr)) { Log("[TraceSixRT] trace pipeline failed (0x%08lX)", (unsigned long)hr); return false; }
    pd.CS = { g_GISixRTAo, sizeof(g_GISixRTAo) };
    hr = g12->CreateComputePipelineState(&pd, IID_PPV_ARGS(&gPsoAo));
    if (FAILED(hr)) { Log("[TraceSixRT] ambient occlusion pipeline failed (0x%08lX): no ray-traced AO", (unsigned long)hr); gPsoAo.Reset(); }
    pd.CS = { g_GISixRTRefl, sizeof(g_GISixRTRefl) };
    hr = g12->CreateComputePipelineState(&pd, IID_PPV_ARGS(&gPsoRefl));
    if (FAILED(hr)) { Log("[TraceSixRT] reflection pipeline failed (0x%08lX): no ray-traced reflections", (unsigned long)hr); gPsoRefl.Reset(); }
    pd.CS = { g_GISixRTSun, sizeof(g_GISixRTSun) };
    hr = g12->CreateComputePipelineState(&pd, IID_PPV_ARGS(&gPsoSun));
    if (FAILED(hr)) { Log("[TraceSixRT] sun shadow pipeline failed (0x%08lX): no ray-traced sun shadows", (unsigned long)hr); gPsoSun.Reset(); }
    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors = (1 + kExtraSets) * kTable; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    g12->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&gHeap));
    gHeapStep = g12->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    for (int i = 0; i < kRing; i++)
    {
        g12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&gAlloc[i]));
        g12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&gAllocSun[i]));
        gLampBuf[i] = MakeBuffer(sizeof(float) * kMaxLamps * kLampFloats, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
        Name(gLampBuf[i].Get(), L"TraceSix lamps");
        gAllocUsed[i] = false;
        if (!gLampBuf[i]) return false;
    }
    g12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, gAlloc[0].Get(), nullptr, IID_PPV_ARGS(&gList));
    Name(gList.Get(), L"TraceSix trace (light, AO, reflections)");
    if (gList) gList->Close();
    g12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, gAllocSun[0].Get(), nullptr, IID_PPV_ARGS(&gListSun));
    Name(gListSun.Get(), L"TraceSix sun shadow");
    if (gListSun) gListSun->Close();
    D3D12_QUERY_HEAP_DESC qh = {};
    // per ring slot: before the trace, after it, after the AO, after the reflections; before and after the sun
    qh.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP; qh.Count = 6 * kRing;
    g12->CreateQueryHeap(&qh, IID_PPV_ARGS(&gQueries));
    gQueryRead = MakeBuffer(8 * 6 * kRing, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST);
    gCompute->GetTimestampFrequency(&gTsFreq);
    return gRoot && gPso && gHeap && gList && gListSun && gQueries && gQueryRead;
}

static void WriteDescriptors()
{
    D3D12_CPU_DESCRIPTOR_HANDLE h = gHeap->GetCPUDescriptorHandleForHeapStart();
    D3D12_SHADER_RESOURCE_VIEW_DESC s = {};
    s.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D; s.Texture2D.MipLevels = 1;
    s.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    s.Format = gRayOTex.fmt;
    g12->CreateShaderResourceView(gRayOTex.res12.Get(), &s, h);
    h.ptr += gHeapStep;
    s.Format = gRayDTex.fmt;
    g12->CreateShaderResourceView(gRayDTex.res12.Get(), &s, h);
    for (int i = 0; i < kOut; i++)
    {
        h.ptr += gHeapStep;
        D3D12_UNORDERED_ACCESS_VIEW_DESC u = {};
        u.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D; u.Format = gOutTex[i].fmt;
        g12->CreateUnorderedAccessView(gOutTex[i].res12.Get(), nullptr, &u, h);
    }
    // which cells hold light: none handed over = a null view (the kernel only reads it when told to)
    h.ptr += gHeapStep;
    s.Format = gOpenTex.res12 ? gOpenTex.fmt : DXGI_FORMAT_R32_FLOAT;
    g12->CreateShaderResourceView(gOpenTex.res12 ? gOpenTex.res12.Get() : nullptr, &s, h);
    // the reuse rays' ends: none handed over = a null view (the kernel only reads it when told to)
    h.ptr += gHeapStep;
    s.Format = gRayVTex.res12 ? gRayVTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateShaderResourceView(gRayVTex.res12 ? gRayVTex.res12.Get() : nullptr, &s, h);
    // the AO's rays in and the AO out: none = null views (CSAo only runs with them)
    h.ptr += gHeapStep;
    s.Format = gAoOTex.res12 ? gAoOTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateShaderResourceView(gAoOTex.res12 ? gAoOTex.res12.Get() : nullptr, &s, h);
    h.ptr += gHeapStep;
    s.Format = gAoNTex.res12 ? gAoNTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateShaderResourceView(gAoNTex.res12 ? gAoNTex.res12.Get() : nullptr, &s, h);
    h.ptr += gHeapStep;
    D3D12_UNORDERED_ACCESS_VIEW_DESC ua = {};
    ua.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D; ua.Format = gAoTex.res12 ? gAoTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateUnorderedAccessView(gAoTex.res12 ? gAoTex.res12.Get() : nullptr, nullptr, &ua, h);
    // the reflection rays in and their ends out: none = null views (CSRefl only runs with them)
    h.ptr += gHeapStep;
    s.Format = gReflOTex.res12 ? gReflOTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateShaderResourceView(gReflOTex.res12 ? gReflOTex.res12.Get() : nullptr, &s, h);
    h.ptr += gHeapStep;
    s.Format = gReflDTex.res12 ? gReflDTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateShaderResourceView(gReflDTex.res12 ? gReflDTex.res12.Get() : nullptr, &s, h);
    for (int i = 0; i < kReflOut; i++)
    {
        h.ptr += gHeapStep;
        ua.Format = gReflOutTex[i].res12 ? gReflOutTex[i].fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
        g12->CreateUnorderedAccessView(gReflOutTex[i].res12 ? gReflOutTex[i].res12.Get() : nullptr, nullptr, &ua, h);
    }
    // the light rays' hit colours out, the material atlases, the sun's rays in and its shadow out (none = null views)
    h.ptr += gHeapStep;
    ua.Format = gAlbTex.res12 ? gAlbTex.fmt : DXGI_FORMAT_R8G8B8A8_UNORM;
    g12->CreateUnorderedAccessView(gAlbTex.res12 ? gAlbTex.res12.Get() : nullptr, nullptr, &ua, h);
    h.ptr += gHeapStep;
    s.Format = gAtlasTex.res12 ? gAtlasTex.fmt : DXGI_FORMAT_R8G8B8A8_UNORM;
    g12->CreateShaderResourceView(gAtlasTex.res12 ? gAtlasTex.res12.Get() : nullptr, &s, h);
    h.ptr += gHeapStep;
    s.Format = gAtlasLowTex.res12 ? gAtlasLowTex.fmt : DXGI_FORMAT_R8G8B8A8_UNORM;
    g12->CreateShaderResourceView(gAtlasLowTex.res12 ? gAtlasLowTex.res12.Get() : nullptr, &s, h);
    h.ptr += gHeapStep;
    s.Format = gSunOTex.res12 ? gSunOTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateShaderResourceView(gSunOTex.res12 ? gSunOTex.res12.Get() : nullptr, &s, h);
    h.ptr += gHeapStep;
    ua.Format = gSunOutTex.res12 ? gSunOutTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateUnorderedAccessView(gSunOutTex.res12 ? gSunOutTex.res12.Get() : nullptr, nullptr, &ua, h);
    h.ptr += gHeapStep;
    s.Format = gLampOTex.res12 ? gLampOTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateShaderResourceView(gLampOTex.res12 ? gLampOTex.res12.Get() : nullptr, &s, h);
    h.ptr += gHeapStep;
    ua.Format = gSunTintTex.res12 ? gSunTintTex.fmt : DXGI_FORMAT_R16G16B16A16_FLOAT;
    g12->CreateUnorderedAccessView(gSunTintTex.res12 ? gSunTintTex.res12.Get() : nullptr, nullptr, &ua, h);
    gDescDirty = false;
}

// (0.24.120; 0.24.125 per set; 0.24.128 reflection sets) A set's table, the heap's table 1 + x: every view the main table has,
// empty, but the atlases (the same as the main table's) and the set's own: a sun set's rays in (t17), shadow out (u11) and glass
// colour out (u12); a reflection set's rays in (t11 starts, t12 directions) and hit, lamp, cell and colour out (u6 - u9). Written
// when its textures or the atlases change (its last list done first: ExtraIssue).
static void WriteExtraDescriptors(int x)
{
    ExtraSun& X = gX[x];
    bool refl = kXRefl[x];
    D3D12_CPU_DESCRIPTOR_HANDLE h0 = gHeap->GetCPUDescriptorHandleForHeapStart();
    h0.ptr += (SIZE_T)(1 + x) * kTable * gHeapStep;
    auto at = [&](int i) { D3D12_CPU_DESCRIPTOR_HANDLE h = h0; h.ptr += (SIZE_T)i * gHeapStep; return h; };
    D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
    sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MipLevels = 1;
    sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    D3D12_UNORDERED_ACCESS_VIEW_DESC u = {};
    u.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    auto srv = [&](int i, SharedTex* t, DXGI_FORMAT none)
    {
        sd.Format = t && t->res12 ? t->fmt : none;
        g12->CreateShaderResourceView(t && t->res12 ? t->res12.Get() : nullptr, &sd, at(i));
    };
    auto uav = [&](int i, SharedTex* t, DXGI_FORMAT none)
    {
        u.Format = t && t->res12 ? t->fmt : none;
        g12->CreateUnorderedAccessView(t && t->res12 ? t->res12.Get() : nullptr, nullptr, &u, at(i));
    };
    const DXGI_FORMAT H = DXGI_FORMAT_R16G16B16A16_FLOAT, C = DXGI_FORMAT_R8G8B8A8_UNORM;
    srv(0, nullptr, H); srv(1, nullptr, H);                                   // t1, t2 the light rays
    for (int i = 0; i < kOut; i++) uav(2 + i, nullptr, H);                   // u0-u4 their ends
    srv(2 + kOut, nullptr, DXGI_FORMAT_R32_FLOAT);                          // t7 the open cells (none: CSRefl's cell checks are off)
    srv(3 + kOut, nullptr, H);                                              // t8 the reuse rays
    srv(4 + kOut, nullptr, H); srv(5 + kOut, nullptr, H);                   // t9, t10 the AO rays
    uav(6 + kOut, nullptr, H);                                              // u5 the AO
    srv(7 + kOut, refl ? &X.o : nullptr, H); srv(8 + kOut, refl ? &X.d : nullptr, H);   // t11, t12 reflection rays
    for (int i = 0; i < kReflOut; i++) uav(9 + kOut + i, refl ? &X.out[i] : nullptr, i == 3 ? C : H);   // u6-u9 their ends
    uav(9 + kOut + kReflOut, nullptr, C);                                   // u10 the hit colours
    srv(10 + kOut + kReflOut, &gAtlasTex, C);                               // t14, t15 the material atlases
    srv(11 + kOut + kReflOut, &gAtlasLowTex, C);
    srv(12 + kOut + kReflOut, refl ? nullptr : &X.o, H);                    // t17 a sun set's rays
    uav(13 + kOut + kReflOut, refl ? nullptr : &X.out[0], H);               // u11 their shadow
    srv(14 + kOut + kReflOut, nullptr, H);                                  // t19 lamp rays: none in a set
    uav(15 + kOut + kReflOut, refl ? nullptr : &X.out[1], H);               // u12 their glass colour
    X.atlasGen = gAtlasGen;
}

static bool EnsureExtraList(int x)
{
    ExtraSun& X = gX[x];
    if (X.list) return true;
    for (int i = 0; i < kRing; i++)
        if (FAILED(g12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&X.alloc[i])))) return false;
    if (FAILED(g12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, X.alloc[0].Get(), nullptr, IID_PPV_ARGS(&X.list)))) return false;
    Name(X.list.Get(), L"TraceSix extra set (scope / glass)");
    X.list->Close();
    D3D12_QUERY_HEAP_DESC qh = {};
    qh.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP; qh.Count = 2 * kRing;
    g12->CreateQueryHeap(&qh, IID_PPV_ARGS(&X.queries));
    X.queryRead = MakeBuffer(8 * 2 * kRing, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST);
    for (int i = 0; i < kRing; i++) { X.allocUsed[i] = false; X.allocFence[i] = 0; }
    if (!X.queries || !X.queryRead) { X.list.Reset(); return false; }
    return true;
}

// (0.24.89) The materials C# drew (RtSetMaterials) for the scene just built: the tile table into a buffer (one entry at
// least - the trace binds it either way), the two atlases shared from D3D11 and copied once (D3D11 then waited for: the
// trace only ever reads them). None, or a texture that will not share: no tiles (the voxels' colour, cutouts by chance).
static void UploadMaterials(UINT64 uvBytes, UINT64 colBytes)
{
    void* atlas;
    void* low;
    int tile, cols;
    std::vector<float> tiles;
    {
        std::lock_guard<std::mutex> l(gSceneMutex);
        atlas = gMatAtlasIn; low = gMatAtlasLowIn; tile = gMatTileIn; cols = gMatColsIn;
        tiles.swap(gMatTilesIn);
        gMatAtlasIn = gMatAtlasLowIn = nullptr;
    }
    gMatTile = gMatCols = gMatTiles = 0;
    gAtlasTex = SharedTex{};
    gAtlasLowTex = SharedTex{};
    gDescDirty = true;
    gAtlasGen++;
    UINT64 bytes = tiles.size() >= kTileFloats ? tiles.size() * 4 : kTileFloats * 4;
    gTileBuf = MakeBuffer(bytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
    Name(gTileBuf.Get(), L"TraceSix material tiles");
    ComPtr<ID3D12Resource> up = MakeBuffer(bytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
    void* p = nullptr;
    if (!gTileBuf || !up || FAILED(up->Map(0, nullptr, &p))) { Log("[TraceSixRT] material table failed: no material colours"); gTileBuf.Reset(); return; }
    memset(p, 0, (size_t)bytes);
    if (!tiles.empty()) memcpy(p, tiles.data(), tiles.size() * 4);
    up->Unmap(0, nullptr);
    BeginBuildList();
    gBuildList->CopyBufferRegion(gTileBuf.Get(), 0, up.Get(), 0, bytes);
    D3D12_RESOURCE_BARRIER b;
    Transition(gTileBuf.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b);
    gBuildList->ResourceBarrier(1, &b);
    if (!SubmitBuildListAndWait()) return;
    if (!atlas || !low || tile <= 0 || cols <= 0 || tiles.size() < kTileFloats)
    {
        Log("[TraceSixRT] No material atlas handed over: hits take the voxels' colour, cutouts stop rays by chance.");
        return;
    }
    if (!EnsureShared(gAtlasTex, atlas, false, "material atlas") || !EnsureShared(gAtlasLowTex, low, false, "small material atlas"))
    {
        gAtlasTex = SharedTex{};
        gAtlasLowTex = SharedTex{};
        Log("[TraceSixRT] The material atlas could not be shared: hits take the voxels' colour.");
        return;
    }
    gCtx11->CopyResource(gAtlasTex.tex11.Get(), (ID3D11Texture2D*)atlas);
    gCtx11->CopyResource(gAtlasLowTex.tex11.Get(), (ID3D11Texture2D*)low);
    UINT64 v = ++gFenceValue;
    gCtx11_4->Signal(gFence11.Get(), v);
    gCtx11->Flush();
    if (!WaitFenceCpu(v)) { GiveUp(DeviceLost() ? "Our D3D12 device was lost" : "The material atlas copy did not finish within 10 s"); return; }
    gMatTile = tile;
    gMatCols = cols;
    gMatTiles = (int)(tiles.size() / kTileFloats);
    const double mb = 1.0 / 1048576.0;
    Log("[TraceSixRT] Materials: %d tiles of %d px (%d a row) in a %ux%u atlas and its %ux%u small copy; texture coordinates %.0f MB, vertex colours %.0f MB.",
        gMatTiles, tile, cols, gAtlasTex.w, gAtlasTex.h, gAtlasLowTex.w, gAtlasLowTex.h, uvBytes * mb, colBytes * mb);
}

static void Issue(int slot)
{
    slot &= kSlots - 1;
    gPending[slot] = 0;
    FrameParams& fs = gFrame[slot];
    FrameParams f;
    {
        std::lock_guard<std::mutex> l(gFrameMutex);
        f = fs;
    }
    if (!gSceneReady || !gTlas || !gGeoBuf || !f.rayO || !f.rayD) return;
    for (int i = 0; i < kOut; i++) if (!f.out[i]) return;
    if (!EnsureDevices((ID3D11Resource*)f.out[0]) || !gSceneReady) return;
    double c0 = NowMs();
    if (!EnsurePipeline()) { gStatus = RT_BUILD_FAILED; gSceneReady = 0; return; }
    static const char* outName[kOut] = { "ray end", "second bounce", "lamp pick", "second lamp pick", "cell read" };
    // ("Ambient Occlusion" = Ray Traced, c[21]: its textures handed over and its pipeline built)
    bool ao = f.aoO && f.aoN && f.ao && f.c[21] > 0.5f && gPsoAo && !gAoFailed;
    void* aoO = ao ? f.aoO : nullptr;
    void* aoN = ao ? f.aoN : nullptr;
    void* aoT = ao ? f.ao : nullptr;
    // (SSRSix's reflection rays: handed over, and the pipeline built)
    bool refl = f.reflO && f.reflD && f.reflOut[0] && f.reflOut[1] && f.reflOut[2] && f.reflOut[3] && gPsoRefl && !gReflFailed;
    void* rO = refl ? f.reflO : nullptr;
    void* rD = refl ? f.reflD : nullptr;
    bool fresh = !gRayOTex.tex11 || gRayOTex.unity != f.rayO || gRayDTex.unity != f.rayD || gOpenUnity != f.open || gRayVUnity != f.rayV
                 || gAoOTex.unity != aoO || gAoNTex.unity != aoN || gAoTex.unity != aoT || gReflOTex.unity != rO || gReflDTex.unity != rD;
    for (int i = 0; i < kReflOut; i++) fresh = fresh || gReflOutTex[i].unity != (refl ? f.reflOut[i] : nullptr);
    // (0.24.89) the light rays' hit colours out; the sun's shadow (c[29]: its switch)
    bool alb = f.alb && !gAlbFailed;
    bool sun = f.sunO && f.sunOut && f.c[29] > 0.5f && gPsoSun && !gSunFailed;
    void* aT = alb ? f.alb : nullptr;
    void* sO = sun ? f.sunO : nullptr;
    void* sT = sun ? f.sunOut : nullptr;
    // (0.24.115) the picture's lamp shadow rays: c[29]'s bit 2, through CSSun (their ends in its output's yzw)
    bool lamp = sun && f.lampO && (((int)(f.c[29] + 0.5f)) & 2) && !gLampFailed;
    void* lO = lamp ? f.lampO : nullptr;
    // (0.24.117) the sun rays' glass colour: c[29]'s bit 4, out of CSSun
    bool tint = sun && f.sunTint && (((int)(f.c[29] + 0.5f)) & 4) && !gTintFailed;
    void* tT = tint ? f.sunTint : nullptr;
    fresh = fresh || gAlbTex.unity != aT || gSunOTex.unity != sO || gSunOutTex.unity != sT || gLampOTex.unity != lO || gSunTintTex.unity != tT || gDescDirty;
    for (int i = 0; i < kOut; i++) fresh = fresh || gOutTex[i].unity != f.out[i];
    // (new textures replace old ones a trace may still be using - and the descriptors it reads: wait it out)
    if (fresh && gFrameNo > 0)
    {
        if (!WaitIdle()) return;   // (not done: the textures it uses are left alone)
        if (gUncollected) { gCtx11_4->Wait(gFence11.Get(), gUncollected); gUncollected = 0; }
    }
    bool ok = EnsureShared(gRayOTex, f.rayO, false, "ray start") && EnsureShared(gRayDTex, f.rayD, false, "ray direction");
    for (int i = 0; i < kOut && ok; i++) ok = EnsureShared(gOutTex[i], f.out[i], true, outName[i]);
    if (ok && f.open) ok = EnsureShared(gOpenTex, f.open, false, "open cells");
    if (!f.open) gOpenTex = SharedTex{};
    gOpenUnity = f.open;
    if (ok && f.rayV) ok = EnsureShared(gRayVTex, f.rayV, false, "reuse rays");
    if (!f.rayV) gRayVTex = SharedTex{};
    gRayVUnity = f.rayV;
    if (!ok) { gStatus = RT_NO_INTEROP; gSceneReady = 0; return; }
    if (ao && !(EnsureShared(gAoOTex, aoO, false, "AO ray start") && EnsureShared(gAoNTex, aoN, false, "AO normal") && EnsureShared(gAoTex, aoT, true, "AO")))
    {
        Log("[TraceSixRT] the ambient occlusion's textures could not be shared: no ray-traced AO this session (the light rays go on)");
        gAoFailed = true;
        ao = false;
        fresh = true;
    }
    if (!ao) { gAoOTex = SharedTex{}; gAoNTex = SharedTex{}; gAoTex = SharedTex{}; }
    if (refl)
    {
        static const char* reflName[kReflOut] = { "reflection hit", "reflection lamp", "reflection cell", "reflection colour" };
        bool rok = EnsureShared(gReflOTex, f.reflO, false, "reflection ray start") && EnsureShared(gReflDTex, f.reflD, false, "reflection ray direction");
        for (int i = 0; i < kReflOut && rok; i++) rok = EnsureShared(gReflOutTex[i], f.reflOut[i], true, reflName[i]);
        if (!rok)
        {
            Log("[TraceSixRT] the reflection rays' textures could not be shared: no ray-traced reflections this session (the light rays go on)");
            gReflFailed = true;
            refl = false;
            fresh = true;
        }
    }
    if (!refl)
    {
        gReflOTex = SharedTex{}; gReflDTex = SharedTex{};
        for (int i = 0; i < kReflOut; i++) gReflOutTex[i] = SharedTex{};
    }
    if (alb && !EnsureShared(gAlbTex, aT, true, "hit colour"))
    {
        Log("[TraceSixRT] the hit colour texture could not be shared: the light rays' ends take the voxels' colour this session");
        gAlbFailed = true;
        alb = false;
        fresh = true;
    }
    if (!alb) gAlbTex = SharedTex{};
    if (sun && !(EnsureShared(gSunOTex, sO, false, "sun ray start") && EnsureShared(gSunOutTex, sT, true, "sun shadow")))
    {
        Log("[TraceSixRT] the sun shadow's textures could not be shared: no ray-traced sun shadows this session");
        gSunFailed = true;
        sun = false;
        fresh = true;
    }
    if (!sun) { gSunOTex = SharedTex{}; gSunOutTex = SharedTex{}; lamp = false; }
    if (lamp && !EnsureShared(gLampOTex, lO, false, "lamp ray start"))
    {
        Log("[TraceSixRT] the lamp rays' start texture could not be shared: no ray-traced direct lamp shadows this session");
        gLampFailed = true;
        lamp = false;
        fresh = true;
    }
    if (!lamp) gLampOTex = SharedTex{};
    if (!sun) tint = false;
    if (tint && !EnsureShared(gSunTintTex, tT, true, "sun glass colour"))
    {
        Log("[TraceSixRT] the sun's glass colour texture could not be shared: sunlight through glass is not tinted this session");
        gTintFailed = true;
        tint = false;
        fresh = true;
    }
    if (!tint) gSunTintTex = SharedTex{};
    if (fresh) WriteDescriptors();
    // (1.0.0) This ring slot's last trace must be done before its allocator is reset and its lamp buffer rewritten (below),
    // and before anything of this frame is handed over. To 1.0.0 it was waited for 1 s and then reset anyway.
    {
        int ring0 = (int)(gFrameNo % kRing);
        if (gAllocUsed[ring0] && !WaitFenceCpu(gAllocFence[ring0]))
        {
            GiveUp(DeviceLost() ? "Our D3D12 device was lost" : "The GPU did not finish a trace within 10 s");
            return;
        }
    }
    // (0.24.136) the extra sets riding this hand-over (the glass's rays: GiGlassSun, placed in GiRtCamera's buffer)
    ExtraGo rideGo[kExtraSets];
    bool rides[kExtraSets] = {};
    int rideRing[kExtraSets] = {};
    for (int x = 0; x < kExtraSets; x++)
    {
        int want;
        {
            std::lock_guard<std::mutex> l(gFrameMutex);
            want = gXRide[x][slot];
        }
        gX[x].rode[slot] = false;
        if (want) rides[x] = ExtraPrepare(x, slot, rideGo[x]);
    }

    // D3D11: a trace still uncollected is waited for first (the fence must only rise: a new hand-over signalled
    // before that trace's end would be lowered again by it, and the next trace would wait forever) ...
    if (gUncollected) { gCtx11_4->Wait(gFence11.Get(), gUncollected); gUncollected = 0; }
    // ... then this frame's rays (and which cells hold light) into the shared textures, and hand them over
    gCtx11->CopyResource(gRayOTex.tex11.Get(), (ID3D11Texture2D*)f.rayO);
    gCtx11->CopyResource(gRayDTex.tex11.Get(), (ID3D11Texture2D*)f.rayD);
    if (f.open) gCtx11->CopyResource(gOpenTex.tex11.Get(), (ID3D11Texture2D*)f.open);
    if (f.rayV) gCtx11->CopyResource(gRayVTex.tex11.Get(), (ID3D11Texture2D*)f.rayV);
    if (ao)
    {
        gCtx11->CopyResource(gAoOTex.tex11.Get(), (ID3D11Texture2D*)f.aoO);
        gCtx11->CopyResource(gAoNTex.tex11.Get(), (ID3D11Texture2D*)f.aoN);
    }
    if (refl)
    {
        gCtx11->CopyResource(gReflOTex.tex11.Get(), (ID3D11Texture2D*)f.reflO);
        gCtx11->CopyResource(gReflDTex.tex11.Get(), (ID3D11Texture2D*)f.reflD);
    }
    if (sun) gCtx11->CopyResource(gSunOTex.tex11.Get(), (ID3D11Texture2D*)f.sunO);
    if (lamp) gCtx11->CopyResource(gLampOTex.tex11.Get(), (ID3D11Texture2D*)f.lampO);
    for (int x = 0; x < kExtraSets; x++) if (rides[x]) ExtraCopyIn(x, rideGo[x]);
    UINT64 handoff = ++gFenceValue;
    gCtx11_4->Signal(gFence11.Get(), handoff);
    gCtx11->Flush();
    gCompute->Wait(gFence12.Get(), handoff);

    // D3D12: the trace, on the compute queue - it runs beside whatever D3D11 draws until Collect. This ring slot's
    // last use must be done before its allocator is reset and its lamp buffer rewritten (it is long done) ...
    int ring = (int)(gFrameNo % kRing);
    // (its last use was waited out before the hand-over, above) ... so its timestamps are in: the GPU time of the trace three frames ago
    if (gAllocUsed[ring])
    {
        UINT64* ts = nullptr;
        D3D12_RANGE rr = { (SIZE_T)(48 * ring), (SIZE_T)(48 * ring + 48) };
        if (SUCCEEDED(gQueryRead->Map(0, &rr, (void**)&ts)))
        {
            UINT64 a = ts[6 * ring], b = ts[6 * ring + 1], c = ts[6 * ring + 2], e = ts[6 * ring + 3], s0 = ts[6 * ring + 4], s1 = ts[6 * ring + 5];
            D3D12_RANGE none = { 0, 0 };
            gQueryRead->Unmap(0, &none);
            if (b > a && gTsFreq)
            {
                double ms = (double)(b - a) * 1000.0 / (double)gTsFreq;
                gTraceSum += ms;
                if (ms > gTraceMax) gTraceMax = ms;
                gTraceN++;
            }
            if (gAoRan[ring] && c > b && gTsFreq)
            {
                gAoSum += (double)(c - b) * 1000.0 / (double)gTsFreq;
                gAoN++;
            }
            if (gReflRan[ring] && e > c && gTsFreq)
            {
                gReflSum += (double)(e - c) * 1000.0 / (double)gTsFreq;
                gReflN++;
            }
            if (gSunRan[ring] && s1 > s0 && gTsFreq)
            {
                gSunSum += (double)(s1 - s0) * 1000.0 / (double)gTsFreq;
                gSunN++;
            }
        }
        {
            double vu = 0.0, vb = 0.0, vs = 0.0;
            if (VramNow(vu, vb, vs))
            {
                if (vu > gVramPeak) gVramPeak = vu;
                if (vs > gVramSysPeak) gVramSysPeak = vs;
                if (gVramBudgetMin <= 0.0 || vb < gVramBudgetMin) gVramBudgetMin = vb;
                if (vu > vb) gVramOver++;
                gVramN++;
            }
        }
        if (gTraceN >= 300)
        {
            if (gVramN > 0 && gVerbose)
                Log("[TraceSixRT] Video memory of this process over these frames: peak %.2f GB of a %.2f GB budget, %d of %d frames over it; moved out to system memory: peak %.2f GB.",
                    gVramPeak, gVramBudgetMin, gVramOver, gVramN, gVramSysPeak);
            gVramPeak = gVramSysPeak = gVramBudgetMin = 0.0;
            gVramOver = gVramN = 0;
            gStats[12] = (float)(gTraceSum / gTraceN);
            gStats[13] = (float)gTraceMax;
            gStats[14] = (float)(gCpuN ? gCpuSum / gCpuN : 0.0);
            gStats[17] = (float)(gAoN ? gAoSum / gAoN : 0.0);
            if (gAoN > 0 && gVerbose)
                Log("[TraceSixRT] Ray-traced ambient occlusion: %.3f ms of GPU time a frame on average over %d frames (%ux%u pixels, %d ray(s) each, %.2f m).",
                    gAoSum / gAoN, gAoN, gAoTex.w, gAoTex.h, (int)f.c[23], f.c[22]);
            gAoSum = 0.0;
            gAoN = 0;
            gStats[18] = (float)(gReflN ? gReflSum / gReflN : 0.0);
            if (gReflN > 0 && gVerbose)
                Log("[TraceSixRT] Ray-traced reflections (ReflectSix's off-screen rays): %.3f ms of GPU time a frame on average over %d frames (%ux%u rays at most, each with a sun ray and a lamp ray at its end).",
                    gReflSum / gReflN, gReflN, gReflOutTex[0].w, gReflOutTex[0].h);
            gReflSum = 0.0;
            gReflN = 0;
            gStats[19] = (float)(gSunN ? gSunSum / gSunN : 0.0);
            if (gSunN > 0 && gVerbose)
                Log("[TraceSixRT] Ray-traced sun shadows: %.3f ms of GPU time a frame on average over %d frames (%ux%u rays, collected while the game lights the frame).",
                    gSunSum / gSunN, gSunN, gSunOutTex.w, gSunOutTex.h);
            gSunSum = 0.0;
            gSunN = 0;
            if (gVerbose)
            Log("[TraceSixRT] Trace: %.3f ms of GPU time a frame on average (longest %.3f) over %d frames, %u rays a frame, each with a sun ray at its end%s%s%s. Render-thread time %.3f ms a frame.",
                gTraceSum / gTraceN, gTraceMax, gTraceN, gOutTex[0].w * gOutTex[0].h,
                f.c[12] > 0.5f ? ", a lamp shadow ray" : "", f.c[13] > 0.5f ? " and a second bounce (with its own sun and lamp rays)" : "",
                gOpenTex.res12 ? (gRayVTex.res12 && f.c[17] > 0.5f ? "; the cell its end reads checked with short rays; a reuse ray" : "; the cell its end reads checked with short rays") : (gRayVTex.res12 && f.c[17] > 0.5f ? "; a reuse ray" : ""),
                gCpuN ? gCpuSum / gCpuN : 0.0);
            gTraceSum = gTraceMax = gCpuSum = 0.0;
            gTraceN = gCpuN = 0;
        }
    }
    {
        void* p = nullptr;
        if (SUCCEEDED(gLampBuf[ring]->Map(0, nullptr, &p)))
        {
            memcpy(p, f.lamps, sizeof(f.lamps));
            gLampBuf[ring]->Unmap(0, nullptr);
        }
    }
    // Doors that moved: their transforms into the instance buffer (once the last refresh, which read it, is done),
    // then the top level refreshed in place - the triangles stay, only where they are placed changes. On the first
    // command list of the frame (the sun's, when it runs).
    std::vector<std::pair<int, std::array<float, 12>>> moved;
    {
        std::lock_guard<std::mutex> l(gInstMutex);
        moved.swap(gInstPending);
        if ((int)moved.size() > kMaxMoved)   // (the rest next frame)
        {
            gInstPending.assign(moved.begin() + kMaxMoved, moved.end());
            moved.resize(kMaxMoved);
        }
    }
    auto refreshDoors = [&](ID3D12GraphicsCommandList4* L)
    {
        bool refresh = false;
        if (!moved.empty() && gInstDef && gTlasUpd)
        {
            if (!gInstUp[ring]) gInstUp[ring] = MakeBuffer(kMaxMoved * 48, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
            BYTE* up = nullptr;
            D3D12_RANGE none = { 0, 0 };
            if (gInstUp[ring] && SUCCEEDED(gInstUp[ring]->Map(0, &none, (void**)&up)))
            {
                D3D12_RESOURCE_BARRIER ib;
                Transition(gInstDef.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST, ib);
                L->ResourceBarrier(1, &ib);
                int k = 0;
                for (const auto& mv : moved)
                {
                    int slot2 = mv.first >= 0 && mv.first < (int)gInstSlot.size() ? gInstSlot[mv.first] : -1;
                    if (slot2 < 0) continue;
                    memcpy(up + 48 * k, mv.second.data(), 48);
                    L->CopyBufferRegion(gInstDef.Get(), (UINT64)slot2 * sizeof(D3D12_RAYTRACING_INSTANCE_DESC), gInstUp[ring].Get(), 48 * (UINT64)k, 48);
                    k++;
                }
                gInstUp[ring]->Unmap(0, nullptr);
                Transition(gInstDef.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, ib);
                L->ResourceBarrier(1, &ib);
                refresh = k > 0;
            }
        }
        if (refresh)
        {
            UavBarrier(L, gTlas.Get());   // (the traces before it read it)
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC ub = {};
            ub.Inputs = gTlasIn;
            ub.Inputs.Flags |= D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PERFORM_UPDATE;
            ub.SourceAccelerationStructureData = gTlas->GetGPUVirtualAddress();
            ub.DestAccelerationStructureData = gTlas->GetGPUVirtualAddress();
            ub.ScratchAccelerationStructureData = gTlasUpd->GetGPUVirtualAddress();
            L->BuildRaytracingAccelerationStructure(&ub, 0, nullptr);
            UavBarrier(L, gTlas.Get());
            if (gRefreshes++ == 0) Log("[TraceSixRT] A door moved: the top level is refreshed in place on the frames something in it moves (%d instance(s) this time).", (int)moved.size());
            gStats[16] = (float)gRefreshes;
        }
    };
    auto bindAll = [&](ID3D12GraphicsCommandList4* L)
    {
        ID3D12DescriptorHeap* heaps[] = { gHeap.Get() };
        L->SetDescriptorHeaps(1, heaps);
        L->SetComputeRootSignature(gRoot.Get());
        L->SetComputeRootShaderResourceView(0, gTlas->GetGPUVirtualAddress());
        L->SetComputeRootDescriptorTable(1, gHeap->GetGPUDescriptorHandleForHeapStart());
        L->SetComputeRootShaderResourceView(2, gGeoBuf->GetGPUVirtualAddress());
        L->SetComputeRootShaderResourceView(3, gIdx->GetGPUVirtualAddress());
        L->SetComputeRootShaderResourceView(4, gPos->GetGPUVirtualAddress());
        L->SetComputeRootShaderResourceView(5, gLampBuf[ring]->GetGPUVirtualAddress());
        L->SetComputeRoot32BitConstants(6, kConst, f.c, 0);
        L->SetComputeRootShaderResourceView(7, (gTileBuf ? gTileBuf : gGeoBuf)->GetGPUVirtualAddress());
        L->SetComputeRootShaderResourceView(8, (gUV ? gUV : gPos)->GetGPUVirtualAddress());
        L->SetComputeRootShaderResourceView(9, (gCol ? gCol : gPos)->GetGPUVirtualAddress());
    };
    // (the atlases: read by every list; shared textures go back to COMMON at the end of each)
    auto atlasRead = [&](D3D12_RESOURCE_BARRIER* bb, int& nn, bool toRead)
    {
        D3D12_RESOURCE_STATES a = toRead ? D3D12_RESOURCE_STATE_COMMON : D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        D3D12_RESOURCE_STATES z = toRead ? D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE : D3D12_RESOURCE_STATE_COMMON;
        if (gAtlasTex.res12) Transition(gAtlasTex.res12.Get(), a, z, bb[nn++]);
        if (gAtlasLowTex.res12) Transition(gAtlasLowTex.res12.Get(), a, z, bb[nn++]);
    };
    // (no atlas here - none drawn, or it would not share - whatever C# asks: hits take the voxels' colour, cutouts by chance)
    if (!gAtlasTex.res12 || !gAtlasLowTex.res12 || gMatTiles == 0) f.c[26] = 0.0f;
    D3D12_RESOURCE_BARRIER b[20 + kOut + kReflOut];
    int nb = 0;

    // 1. The sun's shadow, first and on its own list: its fence value is what the game's lighting waits for
    //    (CollectSun), so it does not wait for the light rays too.
    UINT64 sunDone = 0;
    if (sun)
    {
        gAllocSun[ring]->Reset();
        gListSun->Reset(gAllocSun[ring].Get(), gPsoSun.Get());
        refreshDoors(gListSun.Get());
        nb = 0;
        Transition(gSunOTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
        Transition(gSunOutTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, b[nb++]);
        if (lamp) Transition(gLampOTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
        if (tint) Transition(gSunTintTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, b[nb++]);
        atlasRead(b, nb, true);
        gListSun->ResourceBarrier(nb, b);
        bindAll(gListSun.Get());
        gListSun->EndQuery(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring + 4);
        gListSun->Dispatch((gSunOutTex.w + 7) / 8, (gSunOutTex.h + 7) / 8, 1);
        gListSun->EndQuery(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring + 5);
        gListSun->ResolveQueryData(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring + 4, 2, gQueryRead.Get(), 48 * ring + 32);
        nb = 0;
        Transition(gSunOTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        Transition(gSunOutTex.res12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        if (lamp) Transition(gLampOTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        if (tint) Transition(gSunTintTex.res12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        atlasRead(b, nb, false);
        gListSun->ResourceBarrier(nb, b);
        gListSun->Close();
        ID3D12CommandList* ls[] = { gListSun.Get() };
        gCompute->ExecuteCommandLists(1, ls);
        sunDone = ++gFenceValue;
        gCompute->Signal(gFence12.Get(), sunDone);
    }
    gSunRan[ring] = sun;

    // 2. The light rays, the AO, the reflection rays.
    gAlloc[ring]->Reset();
    gList->Reset(gAlloc[ring].Get(), gPso.Get());
    if (!sun) refreshDoors(gList.Get());
    nb = 0;
    Transition(gRayOTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    Transition(gRayDTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    if (gOpenTex.res12) Transition(gOpenTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    if (gRayVTex.res12) Transition(gRayVTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    for (int i = 0; i < kOut; i++) Transition(gOutTex[i].res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, b[nb++]);
    if (alb) Transition(gAlbTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, b[nb++]);
    if (ao)
    {
        Transition(gAoOTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
        Transition(gAoNTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
        Transition(gAoTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, b[nb++]);
    }
    if (refl)
    {
        Transition(gReflOTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
        Transition(gReflDTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
        for (int i = 0; i < kReflOut; i++) Transition(gReflOutTex[i].res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, b[nb++]);
    }
    atlasRead(b, nb, true);
    gList->ResourceBarrier(nb, b);
    bindAll(gList.Get());
    gList->EndQuery(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring);
    // ("Half-Rate Tracing", c[18]: the traced pixels of this frame's checkerboard only - half the threads, all busy)
    UINT gx = f.c[18] > 0.5f ? (gOutTex[0].w + 1) / 2 : gOutTex[0].w;
    gList->Dispatch((gx + 7) / 8, (gOutTex[0].h + 7) / 8, 1);
    gList->EndQuery(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring + 1);
    // the AO: a pipeline on the same root signature, so the bindings above stay
    if (ao)
    {
        gList->SetPipelineState(gPsoAo.Get());
        gList->Dispatch((gAoTex.w + 7) / 8, (gAoTex.h + 7) / 8, 1);
    }
    gAoRan[ring] = ao;
    gList->EndQuery(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring + 2);
    // SSRSix's reflection rays: the same again, on CSRefl
    if (refl)
    {
        gList->SetPipelineState(gPsoRefl.Get());
        gList->Dispatch((gReflOutTex[0].w + 7) / 8, (gReflOutTex[0].h + 7) / 8, 1);
    }
    gReflRan[ring] = refl;
    gList->EndQuery(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring + 3);
    gList->ResolveQueryData(gQueries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 6 * ring, 4, gQueryRead.Get(), 48 * ring);
    nb = 0;
    Transition(gRayOTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    Transition(gRayDTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    if (gOpenTex.res12) Transition(gOpenTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    if (gRayVTex.res12) Transition(gRayVTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    for (int i = 0; i < kOut; i++) Transition(gOutTex[i].res12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    if (alb) Transition(gAlbTex.res12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    if (ao)
    {
        Transition(gAoOTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        Transition(gAoNTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        Transition(gAoTex.res12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    }
    if (refl)
    {
        Transition(gReflOTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        Transition(gReflDTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
        for (int i = 0; i < kReflOut; i++) Transition(gReflOutTex[i].res12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    }
    atlasRead(b, nb, false);
    gList->ResourceBarrier(nb, b);
    gList->Close();
    ID3D12CommandList* l[] = { gList.Get() };
    gCompute->ExecuteCommandLists(1, l);
    for (int x = 0; x < kExtraSets; x++) if (rides[x]) rideRing[x] = ExtraRun(x, rideGo[x]);   // (after ours, on its signal)
    UINT64 done = ++gFenceValue;
    gCompute->Signal(gFence12.Get(), done);
    for (int x = 0; x < kExtraSets; x++)
        if (rides[x]) { ExtraDone(x, slot, rideRing[x], done, rideGo[x]); gX[x].rode[slot] = true; }
    gAllocFence[ring] = done;
    gAllocUsed[ring] = true;
    gFrameNo++;
    gPending[slot] = done;
    for (int i = 0; i < kOut; i++) gPendingOut[slot][i] = f.out[i];
    gPendingAo[slot] = ao ? f.ao : nullptr;
    for (int i = 0; i < kReflOut; i++) gPendingRefl[slot][i] = refl ? f.reflOut[i] : nullptr;
    gPendingAlb[slot] = alb ? f.alb : nullptr;
    gPendingSun[slot] = sunDone;
    gPendingSunOut[slot] = sun ? f.sunOut : nullptr;
    gPendingSunTint[slot] = tint ? f.sunTint : nullptr;
    gUncollected = done;
    gCpuSum += NowMs() - c0;
    gCpuN++;
    gStats[15] = (float)(NowMs() - c0);   // (the frame-spike line: this trace's render-thread time)
}

// (0.24.89) The sun's shadow, from the game's lighting (the sun's LightEvent.BeforeScreenspaceMask): D3D11 waits for the sun's
// own list only (signalled before the light rays' list), then the shadow goes into the Unity texture. Nothing started for
// this slot: nothing to do.
static void CollectSun(int slot)
{
    slot &= kSlots - 1;
    UINT64 d = gPendingSun[slot];
    if (!d || !gCtx11_4 || !gFence11) return;
    gPendingSun[slot] = 0;
    gCtx11_4->Wait(gFence11.Get(), d);
    if (gSunOutTex.tex11 && gPendingSunOut[slot] == gSunOutTex.unity)
        gCtx11->CopyResource((ID3D11Texture2D*)gPendingSunOut[slot], gSunOutTex.tex11.Get());
    gPendingSunOut[slot] = nullptr;
    if (gPendingSunTint[slot] && gSunTintTex.tex11 && gPendingSunTint[slot] == gSunTintTex.unity)
        gCtx11->CopyResource((ID3D11Texture2D*)gPendingSunTint[slot], gSunTintTex.tex11.Get());
    gPendingSunTint[slot] = nullptr;
}

// D3D11 waits for the slot's trace, then where the rays ended goes back into the Unity textures. Nothing started:
// nothing to do (the textures keep what they held).
static void Collect(int slot)
{
    slot &= kSlots - 1;
    UINT64 done = gPending[slot];
    if (!done || !gCtx11_4 || !gFence11) return;
    gPending[slot] = 0;
    double c0 = NowMs();
    gCtx11_4->Wait(gFence11.Get(), done);
    if (gUncollected == done) gUncollected = 0;
    for (int i = 0; i < kOut; i++)
        if (gOutTex[i].tex11 && gPendingOut[slot][i] == gOutTex[i].unity)
            gCtx11->CopyResource((ID3D11Texture2D*)gPendingOut[slot][i], gOutTex[i].tex11.Get());
    if (gPendingAo[slot] && gAoTex.tex11 && gPendingAo[slot] == gAoTex.unity)
        gCtx11->CopyResource((ID3D11Texture2D*)gPendingAo[slot], gAoTex.tex11.Get());
    gPendingAo[slot] = nullptr;
    for (int i = 0; i < kReflOut; i++)
    {
        if (gPendingRefl[slot][i] && gReflOutTex[i].tex11 && gPendingRefl[slot][i] == gReflOutTex[i].unity)
            gCtx11->CopyResource((ID3D11Texture2D*)gPendingRefl[slot][i], gReflOutTex[i].tex11.Get());
        gPendingRefl[slot][i] = nullptr;
    }
    if (gPendingAlb[slot] && gAlbTex.tex11 && gPendingAlb[slot] == gAlbTex.unity)
        gCtx11->CopyResource((ID3D11Texture2D*)gPendingAlb[slot], gAlbTex.tex11.Get());
    gPendingAlb[slot] = nullptr;
    // (not collected by the lighting this frame - no sun, the lamps' rays only (0.24.115), or the sun's buffers were not
    // run: the light rays' wait covers its list, signalled before theirs)
    if (gPendingSunOut[slot] && gSunOutTex.tex11 && gPendingSunOut[slot] == gSunOutTex.unity)
        gCtx11->CopyResource((ID3D11Texture2D*)gPendingSunOut[slot], gSunOutTex.tex11.Get());
    if (gPendingSunTint[slot] && gSunTintTex.tex11 && gPendingSunTint[slot] == gSunTintTex.unity)
        gCtx11->CopyResource((ID3D11Texture2D*)gPendingSunTint[slot], gSunTintTex.tex11.Get());
    gPendingSun[slot] = 0;
    gPendingSunOut[slot] = nullptr;
    gPendingSunTint[slot] = nullptr;
    // (0.24.136) the riders' outputs (their list ran before this trace's signal)
    for (int x = 0; x < kExtraSets; x++)
        if (gX[x].rode[slot]) { gX[x].rode[slot] = false; ExtraCollect(x, slot); }
    gCpuSum += NowMs() - c0;
}

// (0.24.136) The part of a set's issue before any hand-over: its frame read, its pipeline and list, its textures shared and its
// table written. False: nothing to run for it this frame.
static bool ExtraPrepare(int x, int slot, ExtraGo& g)
{
    ExtraSun& X = gX[x];
    bool refl = kXRefl[x];
    X.pending[slot] = 0;
    for (int i = 0; i < 4; i++) X.pendingOut[slot][i] = nullptr;
    ExtraParams f;
    {
        std::lock_guard<std::mutex> l(gFrameMutex);
        f = X.p[slot];
    }
    if (!gSceneReady || !gTlas || !gGeoBuf || !f.o || !f.out[0] || X.failed) return false;
    if (refl && (!f.d || !f.out[1] || !f.out[2] || !f.out[3])) return false;
    if (!EnsureDevices((ID3D11Resource*)f.out[0]) || !gSceneReady) return false;
    if (!EnsurePipeline()) { gStatus = RT_BUILD_FAILED; gSceneReady = 0; return false; }
    ID3D12PipelineState* pso = refl ? gPsoRefl.Get() : gPsoSun.Get();
    if (!pso || (refl ? gReflFailed : gSunFailed)) return false;
    if (!EnsureExtraList(x)) { Log("[TraceSixRT] a command list could not be made: no ray-traced %s this session", kXName[x]); X.failed = true; return false; }
    // the outputs in use: a sun set's shadow and (bit 4 of c[29]) glass colour; a reflection set's four
    void* want[4] = { f.out[0], nullptr, nullptr, nullptr };
    if (refl) { want[1] = f.out[1]; want[2] = f.out[2]; want[3] = f.out[3]; }
    else if (f.out[1] && (((int)(f.c[29] + 0.5f)) & 4) && !gTintFailed) want[1] = f.out[1];
    bool fresh = !X.o.tex11 || X.o.unity != f.o || (refl && X.d.unity != f.d) || X.atlasGen != gAtlasGen;
    for (int i = 0; i < 4; i++) fresh = fresh || X.out[i].unity != want[i];
    // (new textures or atlases: the set's last list, which read the table, is waited out before it is rewritten)
    if (fresh && (X.no > 0 || gFrameNo > 0))
    {
        if (!WaitIdle()) return false;   // (not done: the textures it uses are left alone)
        if (gUncollected) { gCtx11_4->Wait(gFence11.Get(), gUncollected); gUncollected = 0; }
    }
    // (1.0.0) this set's ring slot: its last list done before ExtraRun resets its allocator (to 1.0.0: 1 s, then reset anyway)
    {
        int ring0 = (int)(X.no % kRing);
        if (X.allocUsed[ring0] && !WaitFenceCpu(X.allocFence[ring0]))
        {
            GiveUp(DeviceLost() ? "Our D3D12 device was lost" : "The GPU did not finish a trace within 10 s");
            return false;
        }
    }
    bool ok = EnsureShared(X.o, f.o, false, "extra ray start") && (!refl || EnsureShared(X.d, f.d, false, "extra ray direction"));
    for (int i = 0; i < 4 && ok; i++) if (want[i]) ok = EnsureShared(X.out[i], want[i], true, "extra ray output");
    if (!ok)
    {
        Log("[TraceSixRT] the textures could not be shared: no ray-traced %s this session", kXName[x]);
        X.failed = true;
        X.o = SharedTex{}; X.d = SharedTex{};
        for (int i = 0; i < 4; i++) X.out[i] = SharedTex{};
        return false;
    }
    if (!refl) X.d = SharedTex{};
    for (int i = 0; i < 4; i++) if (!want[i]) X.out[i] = SharedTex{};
    if (fresh) WriteExtraDescriptors(x);
    g.f = f;
    g.refl = refl;
    g.pso = pso;
    for (int i = 0; i < 4; i++) g.want[i] = want[i];
    return true;
}

// its rays into the shared textures (D3D11, before the hand-over's signal)
static void ExtraCopyIn(int x, const ExtraGo& g)
{
    ExtraSun& X = gX[x];
    gCtx11->CopyResource(X.o.tex11.Get(), (ID3D11Texture2D*)g.f.o);
    if (g.refl) gCtx11->CopyResource(X.d.tex11.Get(), (ID3D11Texture2D*)g.f.d);
}

// its list recorded and run (D3D12, after the hand-over's wait); the caller signals and calls ExtraDone. Its ring slot.
static int ExtraRun(int x, const ExtraGo& g)
{
    ExtraSun& X = gX[x];
    const ExtraParams& f = g.f;
    bool refl = g.refl;
    ID3D12PipelineState* pso = g.pso;
    int ring = (int)(X.no % kRing);   // (its last list was waited out in ExtraPrepare)
    // the GPU time of this ring slot's last trace of the set
    if (X.allocUsed[ring])
    {
        UINT64* ts = nullptr;
        D3D12_RANGE rr = { (SIZE_T)(16 * ring), (SIZE_T)(16 * ring + 16) };
        if (SUCCEEDED(X.queryRead->Map(0, &rr, (void**)&ts)))
        {
            UINT64 a = ts[2 * ring], e = ts[2 * ring + 1];
            D3D12_RANGE none = { 0, 0 };
            X.queryRead->Unmap(0, &none);
            if (e > a && gTsFreq) { X.sum += (double)(e - a) * 1000.0 / (double)gTsFreq; X.n++; }
        }
        if (X.n >= 300)
        {
            gStats[20 + x] = (float)(X.sum / X.n);
            if (gVerbose)
            Log("[TraceSixRT] Ray-traced %s: %.3f ms of GPU time a frame it runs on average over %d frames (%ux%u rays%s).",
                kXName[x], X.sum / X.n, X.n, X.out[0].w, X.out[0].h, !refl && X.out[1].res12 ? ", glass colour along them" : "");
            X.sum = 0.0;
            X.n = 0;
        }
    }
    float c[kConst];
    memcpy(c, f.c, sizeof(c));
    if (!gAtlasTex.res12 || !gAtlasLowTex.res12 || gMatTiles == 0) c[26] = 0.0f;
    ID3D12GraphicsCommandList4* L = X.list.Get();
    X.alloc[ring]->Reset();
    L->Reset(X.alloc[ring].Get(), pso);
    D3D12_RESOURCE_BARRIER b[10];
    int nb = 0;
    Transition(X.o.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    if (refl) Transition(X.d.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    for (int i = 0; i < 4; i++) if (X.out[i].res12) Transition(X.out[i].res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, b[nb++]);
    if (gAtlasTex.res12) Transition(gAtlasTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    if (gAtlasLowTex.res12) Transition(gAtlasLowTex.res12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, b[nb++]);
    L->ResourceBarrier(nb, b);
    ID3D12DescriptorHeap* heaps[] = { gHeap.Get() };
    L->SetDescriptorHeaps(1, heaps);
    L->SetComputeRootSignature(gRoot.Get());
    L->SetComputeRootShaderResourceView(0, gTlas->GetGPUVirtualAddress());
    D3D12_GPU_DESCRIPTOR_HANDLE tbl = gHeap->GetGPUDescriptorHandleForHeapStart();
    tbl.ptr += (UINT64)(1 + x) * kTable * gHeapStep;
    L->SetComputeRootDescriptorTable(1, tbl);
    L->SetComputeRootShaderResourceView(2, gGeoBuf->GetGPUVirtualAddress());
    L->SetComputeRootShaderResourceView(3, gIdx->GetGPUVirtualAddress());
    L->SetComputeRootShaderResourceView(4, gPos->GetGPUVirtualAddress());
    L->SetComputeRootShaderResourceView(5, gLampBuf[0]->GetGPUVirtualAddress());   // (no lamp rays in a set: never read)
    L->SetComputeRoot32BitConstants(6, kConst, c, 0);
    L->SetComputeRootShaderResourceView(7, (gTileBuf ? gTileBuf : gGeoBuf)->GetGPUVirtualAddress());
    L->SetComputeRootShaderResourceView(8, (gUV ? gUV : gPos)->GetGPUVirtualAddress());
    L->SetComputeRootShaderResourceView(9, (gCol ? gCol : gPos)->GetGPUVirtualAddress());
    L->EndQuery(X.queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 2 * ring);
    L->Dispatch((X.out[0].w + 7) / 8, (X.out[0].h + 7) / 8, 1);
    L->EndQuery(X.queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 2 * ring + 1);
    L->ResolveQueryData(X.queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 2 * ring, 2, X.queryRead.Get(), 16 * ring);
    nb = 0;
    Transition(X.o.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    if (refl) Transition(X.d.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    for (int i = 0; i < 4; i++) if (X.out[i].res12) Transition(X.out[i].res12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    if (gAtlasTex.res12) Transition(gAtlasTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    if (gAtlasLowTex.res12) Transition(gAtlasLowTex.res12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON, b[nb++]);
    L->ResourceBarrier(nb, b);
    L->Close();
    ID3D12CommandList* ls[] = { L };
    gCompute->ExecuteCommandLists(1, ls);
    return ring;
}

static void ExtraDone(int x, int slot, int ring, UINT64 done, const ExtraGo& g)
{
    ExtraSun& X = gX[x];
    X.allocFence[ring] = done;
    X.allocUsed[ring] = true;
    X.no++;
    X.pending[slot] = done;
    for (int i = 0; i < 4; i++) X.pendingOut[slot][i] = g.want[i];
}

// (0.24.120; 0.24.125 per set; 0.24.128 reflection sets; 0.24.136 split for the main hand-over's riders) A set's rays (its issue
// event): handed over, its kernel (CSSun or CSRefl) on the set's own list and table. As Issue: a trace still uncollected is waited
// for before D3D11 signals (the fence they share only rises), and this one is left as the uncollected one until ExtraCollect.
static void ExtraIssue(int x, int slot)
{
    slot &= kSlots - 1;
    gX[x].rode[slot] = false;
    ExtraGo g;
    if (!ExtraPrepare(x, slot, g)) return;
    if (gUncollected) { gCtx11_4->Wait(gFence11.Get(), gUncollected); gUncollected = 0; }
    ExtraCopyIn(x, g);
    UINT64 handoff = ++gFenceValue;
    gCtx11_4->Signal(gFence11.Get(), handoff);
    gCtx11->Flush();
    gCompute->Wait(gFence12.Get(), handoff);
    int ring = ExtraRun(x, g);
    UINT64 done = ++gFenceValue;
    gCompute->Signal(gFence12.Get(), done);
    ExtraDone(x, slot, ring, done, g);
    gUncollected = done;
}

// (0.24.120; 0.24.125 per set) A set's collect event: D3D11 waits for its list, then its outputs go into the Unity textures.
// Nothing started for this slot: nothing to do.
static void ExtraCollect(int x, int slot)
{
    ExtraSun& X = gX[x];
    slot &= kSlots - 1;
    UINT64 d = X.pending[slot];
    if (!d || !gCtx11_4 || !gFence11) return;
    X.pending[slot] = 0;
    gCtx11_4->Wait(gFence11.Get(), d);
    if (gUncollected == d) gUncollected = 0;
    for (int i = 0; i < 4; i++)
    {
        if (X.pendingOut[slot][i] && X.out[i].tex11 && X.pendingOut[slot][i] == X.out[i].unity)
            gCtx11->CopyResource((ID3D11Texture2D*)X.pendingOut[slot][i], X.out[i].tex11.Get());
        X.pendingOut[slot][i] = nullptr;
    }
}

static void ReleaseAll()
{
    if (!WaitIdle()) return;   // (the GPU is not done with it: left alone, not freed under it)
    DropScene();
    DropTextures();
    gUncollected = 0;
    gTraceSum = gTraceMax = gCpuSum = 0.0;
    gTraceN = gCpuN = 0;
    if (gStatus == RT_READY) gStatus = RT_NONE;
}

// (0.24.103) "Log Character Meshes": can this driver share a BUFFER between the D3D12 side and D3D11 (made on D3D12 as
// shared, opened on D3D11 - as the textures are)? The characters' skinned vertices would cross that way each frame;
// else through a texture. Also an R32_UINT texture, the fallback that keeps full precision. On the render thread (event 40).
static void ProbeShareBuffer()
{
    if (!g12 || !g11_1) { Log("[TraceSixRT] Buffer sharing: the devices are not up yet (it needs a raid with Trace Rays on)."); return; }
    std::string res;
    char why[220];
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_FLAGS combos[] = { D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS };
    for (D3D12_RESOURCE_FLAGS c : combos)
    {
        D3D12_RESOURCE_DESC rd = {};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = 1u << 20;
        rd.Height = 1;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT_UNKNOWN;
        rd.SampleDesc = {1, 0};
        rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        rd.Flags = c;
        ComPtr<ID3D12Resource> r;
        HRESULT hr = g12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_SHARED, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&r));
        if (FAILED(hr)) { snprintf(why, sizeof(why), "buffer, flags 0x%x: D3D12 create FAILED (0x%08lX); ", (unsigned)c, (unsigned long)hr); res += why; continue; }
        HANDLE h = nullptr;
        hr = g12->CreateSharedHandle(r.Get(), nullptr, GENERIC_ALL, nullptr, &h);
        if (FAILED(hr)) { snprintf(why, sizeof(why), "buffer, flags 0x%x: shared handle FAILED (0x%08lX); ", (unsigned)c, (unsigned long)hr); res += why; continue; }
        ComPtr<ID3D11Buffer> b;
        hr = g11_1->OpenSharedResource1(h, IID_PPV_ARGS(&b));
        CloseHandle(h);
        if (FAILED(hr)) { snprintf(why, sizeof(why), "buffer, flags 0x%x: D3D11 open FAILED (0x%08lX); ", (unsigned)c, (unsigned long)hr); res += why; continue; }
        D3D11_BUFFER_DESC bd = {};
        b->GetDesc(&bd);
        snprintf(why, sizeof(why), "buffer, flags 0x%x: SHARED (D3D11 sees %u bytes, bind 0x%x, misc 0x%x); ", (unsigned)c, bd.ByteWidth, bd.BindFlags, bd.MiscFlags);
        res += why;
    }
    {
        D3D12_RESOURCE_DESC rd = {};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = 1024;
        rd.Height = 64;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT_R32_UINT;
        rd.SampleDesc = {1, 0};
        rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        ComPtr<ID3D12Resource> r;
        HRESULT hr = g12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_SHARED, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&r));
        HANDLE h = nullptr;
        if (SUCCEEDED(hr)) hr = g12->CreateSharedHandle(r.Get(), nullptr, GENERIC_ALL, nullptr, &h);
        ComPtr<ID3D11Texture2D> t;
        if (SUCCEEDED(hr)) { hr = g11_1->OpenSharedResource1(h, IID_PPV_ARGS(&t)); CloseHandle(h); }
        snprintf(why, sizeof(why), "R32_UINT texture: %s (0x%08lX).", SUCCEEDED(hr) ? "SHARED" : "FAILED", (unsigned long)hr);
        res += why;
    }
    Log("[TraceSixRT] Buffer sharing: %s", res.c_str());
}

//--- exports ------------------------------------------------------------------------------------------
// The work under a structured-exception guard: a fault in a driver call turns the test off instead of taking
// the game down. (Its own function: __try cannot share one with C++ objects that need unwinding.)
static void Guarded(int eventId)
{
    __try
    {
        if (eventId == 1) BuildScene();
        else if (eventId == 3) ReleaseAll();
        else if (eventId >= 16 && eventId < 16 + kSlots) Issue(eventId - 16);
        else if (eventId >= 20 && eventId < 20 + kSlots) Collect(eventId - 20);
        else if (eventId >= 24 && eventId < 24 + kSlots) CollectSun(eventId - 24);
        else if (eventId >= 28 && eventId < 28 + kSlots) ExtraIssue(0, eventId - 28);
        else if (eventId >= 32 && eventId < 32 + kSlots) ExtraCollect(0, eventId - 32);
        else if (eventId >= 44 && eventId < 44 + kSlots) ExtraIssue(1, eventId - 44);   // (0.24.125) the glass's sun rays
        else if (eventId >= 48 && eventId < 48 + kSlots) ExtraCollect(1, eventId - 48);
        else if (eventId >= 52 && eventId < 52 + kSlots) ExtraIssue(2, eventId - 52);   // ... in the scope
        else if (eventId >= 56 && eventId < 56 + kSlots) ExtraCollect(2, eventId - 56);
        else if (eventId >= 60 && eventId < 60 + kSlots) ExtraIssue(3, eventId - 60);   // (0.24.128) the glass's reflections
        else if (eventId >= 64 && eventId < 64 + kSlots) ExtraCollect(3, eventId - 64);
        else if (eventId == 40) ProbeShareBuffer();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        gStatus = RT_CRASHED;
        gSceneReady = 0;
    }
}

static void __stdcall OnRenderEvent(int eventId)
{
    std::lock_guard<std::recursive_mutex> l(gRender);
    if (gStatus == RT_CRASHED) return;
    // (1.0.0) our device lost: nothing more sent to it, and D3D11 no longer waits on its fence (Collect) - a wait for a value a
    // lost device never reaches would hold the game's GPU queue for good
    if (DeviceLost()) { GiveUp("Our D3D12 device was lost"); return; }
    Guarded(eventId);
    if (gStatus == RT_CRASHED && !gGaveUp) Log("[TraceSixRT] a fault in event %d: the ray tracing test is off until the game restarts", eventId);
}

RT_EXPORT void* RtGetRenderEventFunc() { return (void*)&OnRenderEvent; }
// What GISix's C# side must match: raised whenever an export's arguments change (2 = 0.24.71: RtSetScene takes
// per-instance flags; 3 = 0.24.75: RtSetFrame takes a job, the open cells and five outputs, 20 constants; 4 = 0.24.76:
// no job again, and a trace is two events, Issue 16 + slot and Collect 20 + slot; 5 = 0.24.77: RtSetFrame takes the
// reuse rays' ends; 6 = 0.24.81: RtUpdateInstances; 7 = 0.24.82: 24 constants; 8 = 0.24.84: RtGetOutputs gone again;
// 9 = 0.24.86: RtSetAo; 10 = 0.24.87: RtSetRefl; 11 = 0.24.89: MeshDesc / SubDesc carry texture coordinates and a tile,
// RtSetMaterials, RtSetExtra, RtSetRefl takes a fourth output, 32 constants, event 24 + slot; 12 = 0.24.94: MeshDesc carries
// the vertex colour; 16 = 0.24.115: RtSetExtra takes the picture's lamp shadow rays; 17 = 0.24.117: and the sun's glass colour out;
// 18 = 0.24.120: RtSetScope, events 28 + slot / 32 + slot; 19 = 0.24.125: RtSetExtraSun, events 44 - 59; 20 = 0.24.128:
// RtSetExtraRefl, events 60 - 67; 21 = 0.24.136: RtSetExtraRide; 22 = 0.24.155: RtSetMaterials takes 12 floats a tile;
// 23 = 0.24.159: RtSetVerbose).
// An older DLL has no such export: GiRt finds that and leaves the ray tracing off.
RT_EXPORT int RtApiVersion() { return 23; }
RT_EXPORT void RtSetVerbose(int on) { gVerbose = on; }

// Placed instances that moved (doors): C#'s instance indices (as RtSetScene took them) and 12 floats each (rows 0-2 of
// the object-to-world matrix). Applied by the next trace (a later one for the same index wins).
RT_EXPORT void RtUpdateInstances(int count, const int* index, const float* mats)
{
    std::lock_guard<std::mutex> l(gInstMutex);
    for (int i = 0; i < count; i++)
    {
        std::array<float, 12> m;
        memcpy(m.data(), mats + 12 * (size_t)i, sizeof(float) * 12);
        bool found = false;
        for (auto& p : gInstPending) if (p.first == index[i]) { p.second = m; found = true; break; }
        if (!found) gInstPending.emplace_back(index[i], m);
    }
}
RT_EXPORT int RtStatus() { return gStatus; }
RT_EXPORT int RtSceneReady() { return gSceneReady; }

// The scene, for event 1 (copied here). subs: 4 ints each (indexStart, indexCount, baseVertex, flags); instMat: 12
// floats each (rows 0-2 of the object-to-world matrix); instFlags: 1 = part of a lamp's own fixture.
// (0.24.102) The scene's material variants, before RtSetScene (see gVarMeshIn).
RT_EXPORT void RtSetVariants(const int* varMesh, const int* varFirst, int varCount, const int* varTiles, int tileCount, const int* instVar, int instCount)
{
    std::lock_guard<std::mutex> l(gSceneMutex);
    gVarMeshIn.assign(varMesh, varMesh + (varCount > 0 ? varCount : 0));
    gVarFirstIn.assign(varFirst, varFirst + (varCount > 0 ? varCount : 0));
    gVarTilesIn.assign(varTiles, varTiles + (tileCount > 0 ? tileCount : 0));
    gInstVarIn.assign(instVar, instVar + (instCount > 0 ? instCount : 0));
}

RT_EXPORT void RtSetScene(const MeshDesc* meshes, int meshCount, const int* subs, int subCount, const int* instMesh, const float* instMat, const int* instFlags, int instCount)
{
    std::lock_guard<std::mutex> l(gSceneMutex);
    gMeshIn.assign(meshes, meshes + meshCount);
    gSubIn.resize(subCount);
    if (subCount > 0) memcpy(gSubIn.data(), subs, sizeof(SubDesc) * subCount);
    gInstMeshIn.assign(instMesh, instMesh + instCount);
    gInstMatIn.assign(instMat, instMat + 12 * (size_t)instCount);
    gInstFlagsIn.assign(instFlags, instFlags + instCount);
}

// A frame's textures (Unity's native pointers: rays' starts, their directions, the reuse rays' ends (null = none),
// which cells hold light (null = no cell reads checked), and the 5 outputs: the hit, the second bounce, the two lamp picks, the cell read), the
// shader's constants (20 floats) and GISix's lamps (64 x 16 floats: pos, dir, col, up), for events 16 / 20 + slot.
RT_EXPORT void RtSetFrame(int slot, void* rayO, void* rayD, void* rayV, void* open, void* const* outs, const float* c, const float* lamps)
{
    std::lock_guard<std::mutex> l(gFrameMutex);
    FrameParams& f = gFrame[slot & (kSlots - 1)];
    f.rayO = rayO; f.rayD = rayD; f.rayV = rayV; f.open = open;
    for (int i = 0; i < kOut; i++) f.out[i] = outs[i];
    memcpy(f.c, c, sizeof(f.c));
    memcpy(f.lamps, lamps, sizeof(f.lamps));
}

// (0.24.86) A frame's ray-traced ambient occlusion: the AO rays' starts and normals (GISix's RTAORAYS) and where the AO
// goes, for the same slot's events (null = none: no AO traced). Its switch, radius and rays are constants 21-23.
RT_EXPORT void RtSetAo(int slot, void* aoO, void* aoN, void* ao)
{
    std::lock_guard<std::mutex> l(gFrameMutex);
    FrameParams& f = gFrame[slot & (kSlots - 1)];
    f.aoO = aoO; f.aoN = aoN; f.ao = ao;
}

// (0.24.87) A frame's reflection rays (SSRSix's: their starts and directions) and where their ends go (the hit, the lamp,
// the cell), for the same slot's events (null = none).
RT_EXPORT void RtSetRefl(int slot, void* reflO, void* reflD, void* hit, void* pick, void* cell, void* col)
{
    std::lock_guard<std::mutex> l(gFrameMutex);
    FrameParams& f = gFrame[slot & (kSlots - 1)];
    f.reflO = reflO; f.reflD = reflD;
    f.reflOut[0] = hit; f.reflOut[1] = pick; f.reflOut[2] = cell; f.reflOut[3] = col;
}

// (0.24.89) A frame's light-ray hit colours out, and the sun's rays in and its shadow out (null = none), for the same
// slot's events (the sun's collected by 24 + slot). The sun's switch and half-angle are constants 29 / 28.
RT_EXPORT void RtSetExtra(int slot, void* alb, void* sunO, void* sunOut, void* lampO, void* sunTint)
{
    std::lock_guard<std::mutex> l(gFrameMutex);
    FrameParams& f = gFrame[slot & (kSlots - 1)];
    f.alb = alb; f.sunO = sunO; f.sunOut = sunOut; f.lampO = lampO; f.sunTint = sunTint;
}

// (0.24.120) A frame's scope sun rays (GiRtScope): its ray starts in, its shadow and glass colour out (null = none), and the
// constants CSSun runs with (32 floats, the scope camera's), for events 28 / 32 + slot.
// (0.24.125) The same for any extra set: 0 the scope's sun (events 28 / 32), 1 the main picture's glass (44 / 48), 2 the scope's
// glass (52 / 56).
RT_EXPORT void RtSetExtraSun(int set, int slot, void* sunO, void* sunOut, void* sunTint, const float* c)
{
    if (set < 0 || set >= kExtraSets || kXRefl[set]) return;
    std::lock_guard<std::mutex> l(gFrameMutex);
    ExtraParams& f = gX[set].p[slot & (kSlots - 1)];
    f.o = sunO; f.d = nullptr; f.out[0] = sunOut; f.out[1] = sunTint; f.out[2] = f.out[3] = nullptr;
    memcpy(f.c, c, sizeof(f.c));
}
// (0.24.128) A reflection set's frame (3 = the main picture's glass, events 60 / 64): its rays' starts (xyz less gCam, w = 1) and
// directions (w = how far), its hit, lamp, cell and colour out (as SSRSix's: RtSetRefl), CSRefl's constants.
RT_EXPORT void RtSetExtraRefl(int set, int slot, void* o, void* d, void* hit, void* pick, void* cell, void* col, const float* c)
{
    if (set < 0 || set >= kExtraSets || !kXRefl[set]) return;
    std::lock_guard<std::mutex> l(gFrameMutex);
    ExtraParams& f = gX[set].p[slot & (kSlots - 1)];
    f.o = o; f.d = d; f.out[0] = hit; f.out[1] = pick; f.out[2] = cell; f.out[3] = col;
    memcpy(f.c, c, sizeof(f.c));
}
RT_EXPORT void RtSetScope(int slot, void* sunO, void* sunOut, void* sunTint, const float* c) { RtSetExtraSun(0, slot, sunO, sunOut, sunTint, c); }
// (0.24.136) Whether a set's rays for this slot ride the main trace's hand-over (events 16 / 20 + slot) instead of their own -
// set every frame by GiGlassSun (1 when GiRtCamera placed them in its own buffer, 0 otherwise).
RT_EXPORT void RtSetExtraRide(int set, int slot, int ride)
{
    if (set < 0 || set >= kExtraSets) return;
    std::lock_guard<std::mutex> l(gFrameMutex);
    gXRide[set][slot & (kSlots - 1)] = ride;
}

// (0.24.89) The materials for the next scene build (event 1): the atlas and its eighth-size copy (Unity's native texture
// pointers; read once, at the build), the tile size and tiles a row, and kTileFloats a tile (tiling / offset; cutoff,
// alpha-tested, painted, glass; (0.24.155) its glow's tile (-1 none), the glow's strength, 0, 0).
RT_EXPORT void RtSetMaterials(void* atlas, void* atlasLow, int tileSize, int cols, const float* tiles, int tileCount)
{
    std::lock_guard<std::mutex> l(gSceneMutex);
    gMatAtlasIn = atlas; gMatAtlasLowIn = atlasLow; gMatTileIn = tileSize; gMatColsIn = cols;
    gMatTilesIn.assign(tiles, tiles + kTileFloats * (size_t)(tileCount > 0 ? tileCount : 0));
}

RT_EXPORT int RtGetStats(float* out, int n)
{
    int k = n < 24 ? n : 24;
    memcpy(out, gStats, k * sizeof(float));
    return k;
}

RT_EXPORT int RtPopLog(char* buf, int cap)
{
    std::lock_guard<std::mutex> l(gLogMutex);
    if (gLogLines.empty() || !buf || cap <= 0) return 0;
    const std::string& s = gLogLines.front();
    int n = (int)s.size() < cap - 1 ? (int)s.size() : cap - 1;
    memcpy(buf, s.c_str(), n);
    buf[n] = 0;
    gLogLines.pop_front();
    return n;
}
