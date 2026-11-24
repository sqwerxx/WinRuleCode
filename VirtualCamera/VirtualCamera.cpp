#include "VirtualCamera.h"
#include <initguid.h>
#include <uuids.h>

DEFINE_GUID(CLSID_VirtualCamera,
    0x12345678, 0x1234, 0x1234, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0);

class CVirtualCamera : public IBaseFilter
{
    LONG m_cRef;

public:
    CVirtualCamera() : m_cRef(1) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (riid == IID_IUnknown || riid == IID_IBaseFilter)
        {
            *ppv = this;
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release()
    {
        LONG ref = InterlockedDecrement(&m_cRef);
        if (ref == 0) delete this;
        return ref;
    }

    // IBaseFilter методы (минимальная реализация)
    STDMETHODIMP GetClassID(CLSID* pClsID) { *pClsID = CLSID_VirtualCamera; return S_OK; }
    STDMETHODIMP Stop() { return S_OK; }
    STDMETHODIMP Pause() { return S_OK; }
    STDMETHODIMP Run(REFERENCE_TIME tStart) { return S_OK; }
    STDMETHODIMP GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State) { *State = State_Running; return S_OK; }
    STDMETHODIMP SetSyncSource(IReferenceClock* pClock) { return S_OK; }
    STDMETHODIMP GetSyncSource(IReferenceClock** pClock) { *pClock = nullptr; return S_OK; }
    STDMETHODIMP EnumPins(IEnumPins** ppEnum) { return E_NOTIMPL; }
    STDMETHODIMP FindPin(LPCWSTR Id, IPin** ppPin) { return E_FAIL; }
    STDMETHODIMP QueryFilterInfo(FILTER_INFO* pInfo)
    {
        wcscpy_s(pInfo->achName, L"WinRule Virtual Camera");
        pInfo->pGraph = nullptr;
        return S_OK;
    }
    STDMETHODIMP JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName) { return S_OK; }
    STDMETHODIMP QueryVendorInfo(LPWSTR* pVendorInfo) { return E_NOTIMPL; }
};

HRESULT RegisterVirtualCamera()
{
    IFilterMapper2* pMapper = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2, (void**)&pMapper);
    if (FAILED(hr)) return hr;

    REGFILTER2 rf2;
    ZeroMemory(&rf2, sizeof(rf2));
    rf2.dwVersion = 2;
    rf2.dwMerit = MERIT_PREFERRED + 1000;
    rf2.cPins2 = 1;

    REGFILTERPINS2 rfp;
    ZeroMemory(&rfp, sizeof(rfp));

    rfp.dwFlags = REG_PINFLAG_B_OUTPUT;
    rfp.cInstances = 1;
    rfp.nMediaTypes = 1;

    REGPINTYPES rpt;
    ZeroMemory(&rpt, sizeof(rpt));
    rpt.clsMajorType = &MEDIATYPE_Video;
    rpt.clsMinorType = &MEDIASUBTYPE_RGB24;
    rfp.lpMediaType = &rpt;

    const GUID pinCategory = PIN_CATEGORY_CAPTURE;
    rfp.clsPinCategory = reinterpret_cast<const GUID*>(&pinCategory);

    rf2.rgPins2 = &rfp;

    // Используем reinterpret_cast для обхода строгой проверки типов
    hr = pMapper->RegisterFilter(
        CLSID_VirtualCamera,
        L"WinRule Virtual Camera",
        nullptr,
        reinterpret_cast<const CLSID*>(&CLSID_VideoInputDeviceCategory),
        nullptr,
        &rf2
    );

    pMapper->Release();
    return hr;
}

HRESULT UnregisterVirtualCamera()
{
    IFilterMapper2* pMapper = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2, (void**)&pMapper);
    if (FAILED(hr)) return hr;

    hr = pMapper->UnregisterFilter(
        reinterpret_cast<const CLSID*>(&CLSID_VideoInputDeviceCategory),
        nullptr,
        CLSID_VirtualCamera
    );

    pMapper->Release();
    return hr;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    return TRUE;
}