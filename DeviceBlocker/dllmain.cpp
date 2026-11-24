#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfreadwrite.h>
#include <dshow.h>
#include <setupapi.h>
#include <psapi.h>
#include <stdio.h>
#include <debugapi.h>
#include <winstring.h>
#include <winternl.h>
#include <inspectable.h>
#include <ntstatus.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <mmsystem.h>
#include <functiondiscoverykeys_devpkey.h>
#include <shellapi.h>
#include "MinHook.h"

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "runtimeobject.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "propsys.lib")
#pragma comment(lib, "shell32.lib")

// ===== ТИПЫ ФУНКЦИЙ =====
typedef HRESULT(WINAPI* MFEnumDeviceSources_t)(IMFAttributes*, IMFActivate***, UINT32*);
typedef HRESULT(WINAPI* MFCreateDeviceSource_t)(IMFAttributes*, IMFMediaSource**);
typedef HRESULT(WINAPI* CoCreateInstance_t)(REFCLSID, LPUNKNOWN, DWORD, REFIID, LPVOID*);
typedef HANDLE(WINAPI* CreateFileW_t)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef HANDLE(WINAPI* CreateFileA_t)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef HDEVINFO(WINAPI* SetupDiGetClassDevsW_t)(const GUID*, PCWSTR, HWND, DWORD);
typedef HRESULT(WINAPI* RoActivateInstance_t)(HSTRING, IInspectable**);
typedef NTSTATUS(WINAPI* NtCreateFile_t)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK,
    PLARGE_INTEGER, ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
typedef NTSTATUS(WINAPI* NtOpenFile_t)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, ULONG, ULONG);
typedef MMRESULT(WINAPI* waveInOpen_t)(LPHWAVEIN, UINT, LPCWAVEFORMATEX, DWORD_PTR, DWORD_PTR, DWORD);
typedef UINT(WINAPI* waveInGetNumDevs_t)();
typedef MMRESULT(WINAPI* waveInGetDevCapsW_t)(UINT_PTR, LPWAVEINCAPSW, UINT);
typedef HRESULT(WINAPI* ActivateAudioInterfaceAsync_t)(
    LPCWSTR, REFIID, PROPVARIANT*,
    IActivateAudioInterfaceCompletionHandler*,
    IActivateAudioInterfaceAsyncOperation**);
// НОВОЕ: для блокировки админки
typedef BOOL(WINAPI* ShellExecuteExW_t)(LPSHELLEXECUTEINFOW);

// ===== УКАЗАТЕЛИ НА ОРИГИНАЛЫ =====
MFEnumDeviceSources_t Original_MFEnumDeviceSources = nullptr;
MFCreateDeviceSource_t Original_MFCreateDeviceSource = nullptr;
CoCreateInstance_t Original_CoCreateInstance = nullptr;
CreateFileW_t Original_CreateFileW = nullptr;
CreateFileA_t Original_CreateFileA = nullptr;
SetupDiGetClassDevsW_t Original_SetupDiGetClassDevsW = nullptr;
RoActivateInstance_t Original_RoActivateInstance = nullptr;
NtCreateFile_t Original_NtCreateFile = nullptr;
NtOpenFile_t Original_NtOpenFile = nullptr;
waveInOpen_t Original_waveInOpen = nullptr;
waveInGetNumDevs_t Original_waveInGetNumDevs = nullptr;
waveInGetDevCapsW_t Original_waveInGetDevCapsW = nullptr;
ActivateAudioInterfaceAsync_t Original_ActivateAudioInterfaceAsync = nullptr;
ShellExecuteExW_t Original_ShellExecuteExW = nullptr;

// ===== ГЛОБАЛЬНЫЕ ПЕРЕМЕННЫЕ =====
bool g_BlockCamera = false;
bool g_BlockMicrophone = false;
bool g_BlockLocation = false;       // 🆕 НОВОЕ
bool g_BlockAdminRights = false;    // 🆕 НОВОЕ
wchar_t** g_BlockedPaths = nullptr;
int g_BlockedPathsCount = 0;

// ===== GUID'ы устройств =====
static const GUID OLD_VIDCAP_GUID = { 0x860BB310, 0x5D01, 0x11d0,{ 0xBD, 0x3B, 0x00, 0xA0, 0xC9, 0x11, 0xCE, 0x86 } };
static const GUID NEW_VIDCAP_GUID = { 0x8AC3587A, 0x4AE7, 0x42D8,{ 0x99, 0xE0, 0x0A, 0x60, 0x13, 0xEE, 0xF9, 0x0F } };
static const GUID OLD_AUDCAP_GUID = { 0xD24AED64, 0x7538, 0x4669,{ 0x81, 0x52, 0x3C, 0x9E, 0x24, 0xD8, 0xC8, 0xBE } };
static const GUID NEW_AUDCAP_GUID = { 0xE5323777, 0xF976, 0x4F5B,{ 0x9B, 0x55, 0xB9, 0x46, 0x99, 0xC4, 0x6E, 0x44 } };

static const IID IID_IAudioClient = { 0x1CB9AD4C, 0xDB84, 0x4357,{ 0x94, 0x62, 0x9D, 0x75, 0x87, 0x19, 0x49, 0xBE } };
static const IID IID_IAudioClient2 = { 0x726778CD, 0xF60A, 0x4eda,{ 0x82, 0xDE, 0xA4, 0x76, 0x18, 0x1A, 0xC1, 0xB1 } };
static const IID IID_IAudioClient3 = { 0x7ED4EE07, 0x8E67, 0x4CD4,{ 0x8C, 0x1A, 0x2B, 0x7A, 0x59, 0x87, 0xAD, 0x42 } };
static const IID IID_IAudioCaptureClient = { 0xC8ADBD64, 0xE71E, 0x48a0,{ 0xA4, 0xDE, 0x18, 0x5C, 0x39, 0x5C, 0xD3, 0x17 } };

// 🆕 CLSID для геолокации Windows
static const CLSID CLSID_Location = { 0xE9020017, 0xD1C0, 0x4000,{ 0xA4, 0xF4, 0xB0, 0x9B, 0x74, 0xC3, 0x03, 0x06 } };
static const IID IID_ILocation = { 0xAB2ECE69, 0x56D9, 0x4F28,{ 0xB5, 0x25, 0x06, 0x7E, 0x8A, 0x55, 0xC1, 0x20 } };

bool IsVideoType(const GUID& guidCategory)
{
    return IsEqualGUID(guidCategory, OLD_VIDCAP_GUID) ||
        IsEqualGUID(guidCategory, NEW_VIDCAP_GUID) ||
        IsEqualGUID(guidCategory, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
}

bool IsAudioType(const GUID& guidCategory)
{
    return IsEqualGUID(guidCategory, OLD_AUDCAP_GUID) ||
        IsEqualGUID(guidCategory, NEW_AUDCAP_GUID) ||
        IsEqualGUID(guidCategory, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID);
}

void FreeBlockedPaths()
{
    if (g_BlockedPaths != nullptr)
    {
        for (int i = 0; i < g_BlockedPathsCount; i++)
            if (g_BlockedPaths[i] != nullptr) delete[] g_BlockedPaths[i];
        delete[] g_BlockedPaths;
        g_BlockedPaths = nullptr;
        g_BlockedPathsCount = 0;
    }
}

void ReadBlockedPathsFromRegistry()
{
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    wchar_t regKeyPath[1024] = L"Software\\WinRule\\BlockedApps\\";
    for (int i = 0; exePath[i] != L'\0'; i++)
    {
        if (exePath[i] == L'\\') wcscat_s(regKeyPath, L"_");
        else { size_t len = wcslen(regKeyPath); regKeyPath[len] = exePath[i]; regKeyPath[len + 1] = L'\0'; }
    }
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, regKeyPath, 0, KEY_READ, &hKey) == ERROR_SUCCESS)
    {
        DWORD dataSize = 0;
        if (RegQueryValueExW(hKey, L"BlockedPaths", nullptr, nullptr, nullptr, &dataSize) == ERROR_SUCCESS && dataSize > 0)
        {
            wchar_t* dataBuffer = new wchar_t[dataSize / sizeof(wchar_t)];
            DWORD bufferSize = dataSize;
            if (RegQueryValueExW(hKey, L"BlockedPaths", nullptr, nullptr, (LPBYTE)dataBuffer, &bufferSize) == ERROR_SUCCESS)
            {
                int count = 0;
                wchar_t* ptr = dataBuffer;
                while (*ptr != L'\0') { count++; ptr += wcslen(ptr) + 1; }
                if (count > 0)
                {
                    FreeBlockedPaths();
                    g_BlockedPaths = new wchar_t* [count];
                    g_BlockedPathsCount = count;
                    ptr = dataBuffer;
                    for (int i = 0; i < count; i++)
                    {
                        size_t len = wcslen(ptr);
                        g_BlockedPaths[i] = new wchar_t[len + 1];
                        wcscpy_s(g_BlockedPaths[i], len + 1, ptr);
                        ptr += len + 1;
                    }
                    char buf[256];
                    sprintf_s(buf, "[WinRule] Loaded %d blocked paths\n", count);
                    OutputDebugStringA(buf);
                }
            }
            delete[] dataBuffer;
        }
        RegCloseKey(hKey);
    }
}

bool IsPathBlocked(LPCWSTR path)
{
    if (path == nullptr || g_BlockedPaths == nullptr || g_BlockedPathsCount == 0) return false;
    LPCWSTR checkPath = path;
    if (path[0] == L'\\' && path[1] == L'\\')
    {
        if (path[2] == L'.' && path[3] == L'\\') checkPath = path + 4;
        else if (path[2] == L'?' && path[3] == L'\\')
        {
            checkPath = path + 4;
            if (_wcsnicmp(checkPath, L"UNC\\", 4) == 0) checkPath += 4;
        }
    }
    for (int i = 0; i < g_BlockedPathsCount; i++)
    {
        if (g_BlockedPaths[i] == nullptr) continue;
        size_t blockedLen = wcslen(g_BlockedPaths[i]);
        if (blockedLen == 0) continue;
        size_t compareLen = blockedLen;
        if (g_BlockedPaths[i][blockedLen - 1] == L'\\') compareLen--;
        if (_wcsnicmp(checkPath, g_BlockedPaths[i], compareLen) == 0)
        {
            wchar_t nextChar = checkPath[compareLen];
            if (nextChar == L'\\' || nextChar == L'\0') return true;
        }
    }
    return false;
}

void ReadSettingsFromRegistry()
{
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    wchar_t regKeyPath[1024] = L"Software\\WinRule\\BlockedApps\\";
    for (int i = 0; exePath[i] != L'\0'; i++)
    {
        if (exePath[i] == L'\\') wcscat_s(regKeyPath, L"_");
        else { size_t len = wcslen(regKeyPath); regKeyPath[len] = exePath[i]; regKeyPath[len + 1] = L'\0'; }
    }
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, regKeyPath, 0, KEY_READ, &hKey) == ERROR_SUCCESS)
    {
        DWORD val = 0, sz = sizeof(DWORD);
        if (RegQueryValueExW(hKey, L"BlockCamera", nullptr, nullptr, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
            g_BlockCamera = (val != 0);
        val = 0; sz = sizeof(DWORD);
        if (RegQueryValueExW(hKey, L"BlockMicrophone", nullptr, nullptr, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
            g_BlockMicrophone = (val != 0);
        // 🆕 НОВОЕ: читаем настройки геолокации и админки
        val = 0; sz = sizeof(DWORD);
        if (RegQueryValueExW(hKey, L"BlockLocation", nullptr, nullptr, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
            g_BlockLocation = (val != 0);
        val = 0; sz = sizeof(DWORD);
        if (RegQueryValueExW(hKey, L"BlockAdminRights", nullptr, nullptr, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
            g_BlockAdminRights = (val != 0);

        char buf[256];
        sprintf_s(buf, "[WinRule] Settings: Cam=%d, Mic=%d, Loc=%d, Admin=%d\n",
            g_BlockCamera, g_BlockMicrophone, g_BlockLocation, g_BlockAdminRights);
        OutputDebugStringA(buf);
        RegCloseKey(hKey);
    }
}

bool IsCaptureDevicePath(LPCWSTR path)
{
    if (path == nullptr) return false;
    bool isDevicePath = false;
    if (path[0] == L'\\' && path[1] == L'\\')
    {
        if ((path[2] == L'?' || path[2] == L'.') && path[3] == L'\\')
            isDevicePath = true;
    }
    else if (path[0] == L'\\' && (path[1] == L'D' || path[1] == L'd'))
    {
        if (_wcsnicmp(path, L"\\Device\\", 8) == 0)
            isDevicePath = true;
    }
    if (!isDevicePath) return false;

    if (g_BlockCamera)
    {
        if (wcsstr(path, L"#vid_") != nullptr) return true;
        if (wcsstr(path, L"{65E8773D-8F56-11D0-A3B9-00A0C9223196}") != nullptr) return true;
        if (wcsstr(path, L"{6994AD04-93EF-11D0-A3CC-00A0C9223196}") != nullptr) return true;
        if (wcsstr(path, L"{65E8773E-8F56-11D0-A3B9-00A0C9223196}") != nullptr) return true;
    }
    if (g_BlockMicrophone)
    {
        if (wcsstr(path, L"MMDEVAPI") != nullptr) return true;
        if (wcsstr(path, L"{0.0.1.") != nullptr) return true;
        if (wcsstr(path, L"#aud_") != nullptr) return true;
        if (wcsstr(path, L"{6994AD05-93EF-11D0-A3CC-00A0C9223196}") != nullptr) return true;
    }
    return false;
}

// ===== ХУК MFEnumDeviceSources =====
HRESULT WINAPI Hooked_MFEnumDeviceSources(IMFAttributes* pAttributes, IMFActivate*** pppSourceActivate, UINT32* pcCount)
{
    if (pAttributes == nullptr) { *pppSourceActivate = nullptr; *pcCount = 0; return S_OK; }
    HRESULT hr = Original_MFEnumDeviceSources(pAttributes, pppSourceActivate, pcCount);
    if (FAILED(hr) || *pcCount == 0 || *pppSourceActivate == nullptr) return hr;
    if (!g_BlockCamera && !g_BlockMicrophone) return S_OK;
    IMFActivate** tempArray = new IMFActivate * [*pcCount];
    UINT32 filteredCount = 0;
    for (UINT32 i = 0; i < *pcCount; i++)
    {
        IMFActivate* pActivate = (*pppSourceActivate)[i];
        if (pActivate == nullptr) continue;
        GUID guidCategory = { 0 };
        HRESULT hrDevType = pActivate->GetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, &guidCategory);
        bool isVideo = false, isAudio = false;
        if (SUCCEEDED(hrDevType)) { isVideo = IsVideoType(guidCategory); isAudio = IsAudioType(guidCategory); }
        if (isVideo && g_BlockCamera) { pActivate->Release(); continue; }
        if (isAudio && g_BlockMicrophone) { pActivate->Release(); continue; }
        tempArray[filteredCount++] = pActivate;
    }
    CoTaskMemFree(*pppSourceActivate);
    if (filteredCount == 0) { *pppSourceActivate = nullptr; *pcCount = 0; delete[] tempArray; return S_OK; }
    *pppSourceActivate = (IMFActivate**)CoTaskMemAlloc(filteredCount * sizeof(IMFActivate*));
    if (*pppSourceActivate == nullptr) { for (UINT32 i = 0; i < filteredCount; i++) tempArray[i]->Release(); *pcCount = 0; delete[] tempArray; return E_OUTOFMEMORY; }
    for (UINT32 i = 0; i < filteredCount; i++) (*pppSourceActivate)[i] = tempArray[i];
    *pcCount = filteredCount;
    delete[] tempArray;
    return S_OK;
}

// ===== ХУК MFCreateDeviceSource =====
HRESULT WINAPI Hooked_MFCreateDeviceSource(IMFAttributes* pAttributes, IMFMediaSource** ppSource)
{
    if (pAttributes == nullptr) { *ppSource = nullptr; return MF_E_NOT_FOUND; }
    GUID guidCategory = { 0 };
    HRESULT hr = pAttributes->GetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, &guidCategory);
    if (SUCCEEDED(hr))
    {
        if (IsVideoType(guidCategory) && g_BlockCamera) { *ppSource = nullptr; return MF_E_NOT_FOUND; }
        if (IsAudioType(guidCategory) && g_BlockMicrophone) { *ppSource = nullptr; return MF_E_NOT_FOUND; }
    }
    return Original_MFCreateDeviceSource(pAttributes, ppSource);
}

// ===== ХУК CoCreateInstance (с блокировкой геолокации) =====
HRESULT WINAPI Hooked_CoCreateInstance(REFCLSID rclsid, LPUNKNOWN pUnkOuter, DWORD dwClsContext, REFIID riid, LPVOID* ppv)
{
    // Камера
    if (g_BlockCamera && IsEqualCLSID(rclsid, CLSID_SystemDeviceEnum) && IsEqualIID(riid, IID_ICreateDevEnum))
    {
        OutputDebugStringW(L"[WinRule] >>> SystemDeviceEnum BLOCKED (camera)\n");
        *ppv = nullptr; return REGDB_E_CLASSNOTREG;
    }

    // Микрофон
    if (g_BlockMicrophone)
    {
        if (IsEqualCLSID(rclsid, __uuidof(MMDeviceEnumerator)))
        {
            *ppv = nullptr; return REGDB_E_CLASSNOTREG;
        }
        static const CLSID CLSID_AudioRecord = { 0xb5d4f751, 0x49f6, 0x4675,{ 0x87, 0x3e, 0x8a, 0x70, 0x36, 0x05, 0x56, 0xf8 } };
        static const CLSID CLSID_DirectSoundCapture = { 0xb0210780, 0x89cd, 0x11d0,{ 0xaf, 0x0e, 0x00, 0xa0, 0xc9, 0x25, 0xcd, 0x16 } };
        static const CLSID CLSID_DirectSoundFullDuplex = { 0xfea4300c, 0x7959, 0x11d2,{ 0xb7, 0x9b, 0x00, 0x00, 0xf8, 0x03, 0x22, 0x12 } };
        if (IsEqualCLSID(rclsid, CLSID_AudioRecord) ||
            IsEqualCLSID(rclsid, CLSID_DirectSoundCapture) ||
            IsEqualCLSID(rclsid, CLSID_DirectSoundFullDuplex))
        {
            *ppv = nullptr; return REGDB_E_CLASSNOTREG;
        }
    }

    // 🆕 НОВОЕ: Блокировка геолокации через COM
    if (g_BlockLocation)
    {
        if (IsEqualCLSID(rclsid, CLSID_Location))
        {
            OutputDebugStringW(L"[WinRule] >>> CLSID_Location BLOCKED (geolocation)\n");
            *ppv = nullptr; return REGDB_E_CLASSNOTREG;
        }
        if (IsEqualIID(riid, IID_ILocation))
        {
            OutputDebugStringW(L"[WinRule] >>> IID_ILocation BLOCKED (geolocation)\n");
            *ppv = nullptr; return E_NOINTERFACE;
        }
    }

    return Original_CoCreateInstance(rclsid, pUnkOuter, dwClsContext, riid, ppv);
}

// ===== ХУК CreateFileW =====
HANDLE WINAPI Hooked_CreateFileW(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
    LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition, DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
{
    if (lpFileName != nullptr)
    {
        if (IsCaptureDevicePath(lpFileName))
        {
            SetLastError(ERROR_ACCESS_DENIED); return INVALID_HANDLE_VALUE;
        }
        if (IsPathBlocked(lpFileName))
        {
            OutputDebugStringW(L"[WinRule] CreateFileW PATH BLOCKED: "); OutputDebugStringW(lpFileName); OutputDebugStringW(L"\n");
            SetLastError(ERROR_ACCESS_DENIED); return INVALID_HANDLE_VALUE;
        }
    }
    return Original_CreateFileW(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
}

// ===== ХУК CreateFileA =====
HANDLE WINAPI Hooked_CreateFileA(LPCSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
    LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition, DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
{
    if (lpFileName != nullptr)
    {
        wchar_t widePath[MAX_PATH * 2];
        MultiByteToWideChar(CP_ACP, 0, lpFileName, -1, widePath, MAX_PATH * 2);
        if (IsCaptureDevicePath(widePath) || IsPathBlocked(widePath))
        {
            SetLastError(ERROR_ACCESS_DENIED); return INVALID_HANDLE_VALUE;
        }
    }
    return Original_CreateFileA(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
}

// ===== ХУК SetupDiGetClassDevsW =====
HDEVINFO WINAPI Hooked_SetupDiGetClassDevsW(const GUID* ClassGuid, PCWSTR Enumerator, HWND hwndParent, DWORD Flags)
{
    if (ClassGuid != nullptr)
    {
        static const GUID GUID_DEVCLASS_CAMERA = { 0xCA3E7AB9, 0xB4C3, 0x4AE6,{ 0x82, 0x51, 0x57, 0x9E, 0xF9, 0x33, 0x89, 0x0F } };
        static const GUID GUID_DEVCLASS_IMAGE = { 0x6BDD1FC6, 0x810F, 0x11D0,{ 0xBE, 0xC7, 0x08, 0x00, 0x2B, 0xE2, 0x09, 0x2F } };
        static const GUID GUID_DEVCLASS_AUDIOENDPOINT = { 0x4D36E96C, 0xE325, 0x11CE,{ 0xBF, 0xC1, 0x08, 0x00, 0x2B, 0xE1, 0x03, 0x18 } };
        if (g_BlockCamera && (IsEqualGUID(*ClassGuid, GUID_DEVCLASS_CAMERA) || IsEqualGUID(*ClassGuid, GUID_DEVCLASS_IMAGE)))
        {
            SetLastError(ERROR_NO_MORE_ITEMS); return INVALID_HANDLE_VALUE;
        }
        if (g_BlockMicrophone && IsEqualGUID(*ClassGuid, GUID_DEVCLASS_AUDIOENDPOINT))
        {
            SetLastError(ERROR_NO_MORE_ITEMS); return INVALID_HANDLE_VALUE;
        }
    }
    return Original_SetupDiGetClassDevsW(ClassGuid, Enumerator, hwndParent, Flags);
}

// ===== ХУК RoActivateInstance (с блокировкой геолокации) =====
HRESULT WINAPI Hooked_RoActivateInstance(HSTRING activatableClassId, IInspectable** instance)
{
    UINT32 length = 0;
    PCWSTR str = WindowsGetStringRawBuffer(activatableClassId, &length);
    if (str != nullptr)
    {
        // Камера
        if (g_BlockCamera && (wcsstr(str, L"MediaCapture") != nullptr || wcsstr(str, L"MediaFrameReader") != nullptr || wcsstr(str, L"MediaFrameSource") != nullptr))
        {
            *instance = nullptr; return E_ACCESSDENIED;
        }

        // Микрофон
        if (g_BlockMicrophone)
        {
            if (wcsstr(str, L"AudioGraph") != nullptr || wcsstr(str, L"AudioDeviceInput") != nullptr ||
                wcsstr(str, L"AudioCapture") != nullptr || wcsstr(str, L"DeviceInformation") != nullptr ||
                wcsstr(str, L"DeviceWatcher") != nullptr || wcsstr(str, L"MediaDevice") != nullptr ||
                wcsstr(str, L"DeviceEnumerationHelper") != nullptr || wcsstr(str, L"Windows.Internal.DeviceEnumerationHelper") != nullptr ||
                wcsstr(str, L"Windows.Media.Devices") != nullptr)
            {
                OutputDebugStringW(L"[WinRule] >>> WinRT BLOCKED (mic): "); OutputDebugStringW(str); OutputDebugStringW(L"\n");
                *instance = nullptr; return E_ACCESSDENIED;
            }
        }

        // 🆕 НОВОЕ: Блокировка геолокации через WinRT
        if (g_BlockLocation)
        {
            if (wcsstr(str, L"Windows.Devices.Geolocation") != nullptr ||
                wcsstr(str, L"Geolocator") != nullptr ||
                wcsstr(str, L"Geolocation") != nullptr)
            {
                OutputDebugStringW(L"[WinRule] >>> WinRT BLOCKED (location): "); OutputDebugStringW(str); OutputDebugStringW(L"\n");
                *instance = nullptr; return E_ACCESSDENIED;
            }
        }
    }
    return Original_RoActivateInstance(activatableClassId, instance);
}

// ===== ХУК NtCreateFile =====
NTSTATUS WINAPI Hooked_NtCreateFile(PHANDLE FileHandle, ACCESS_MASK DesiredAccess, POBJECT_ATTRIBUTES ObjectAttributes,
    PIO_STATUS_BLOCK IoStatusBlock, PLARGE_INTEGER AllocationSize, ULONG FileAttributes,
    ULONG ShareAccess, ULONG CreateDisposition, ULONG CreateOptions, PVOID EaBuffer, ULONG EaLength)
{
    if (ObjectAttributes && ObjectAttributes->ObjectName && ObjectAttributes->ObjectName->Buffer)
    {
        LPCWSTR path = ObjectAttributes->ObjectName->Buffer;
        if (IsCaptureDevicePath(path))
            return STATUS_ACCESS_DENIED;
        if (IsPathBlocked(path))
        {
            OutputDebugStringW(L"[WinRule] NtCreateFile PATH BLOCKED: "); OutputDebugStringW(path); OutputDebugStringW(L"\n");
            return STATUS_ACCESS_DENIED;
        }
    }
    return Original_NtCreateFile(FileHandle, DesiredAccess, ObjectAttributes, IoStatusBlock, AllocationSize, FileAttributes, ShareAccess, CreateDisposition, CreateOptions, EaBuffer, EaLength);
}

// ===== ХУК NtOpenFile =====
NTSTATUS WINAPI Hooked_NtOpenFile(PHANDLE FileHandle, ACCESS_MASK DesiredAccess, POBJECT_ATTRIBUTES ObjectAttributes,
    PIO_STATUS_BLOCK IoStatusBlock, ULONG ShareAccess, ULONG OpenOptions)
{
    if (ObjectAttributes && ObjectAttributes->ObjectName && ObjectAttributes->ObjectName->Buffer)
    {
        LPCWSTR path = ObjectAttributes->ObjectName->Buffer;
        if (IsCaptureDevicePath(path))
            return STATUS_ACCESS_DENIED;
        if (IsPathBlocked(path))
        {
            OutputDebugStringW(L"[WinRule] NtOpenFile PATH BLOCKED: "); OutputDebugStringW(path); OutputDebugStringW(L"\n");
            return STATUS_ACCESS_DENIED;
        }
    }
    return Original_NtOpenFile(FileHandle, DesiredAccess, ObjectAttributes, IoStatusBlock, ShareAccess, OpenOptions);
}

// ===== ХУКИ WaveIn =====
MMRESULT WINAPI Hooked_waveInOpen(LPHWAVEIN phwi, UINT uDeviceID, LPCWAVEFORMATEX pwfx, DWORD_PTR dwCallback, DWORD_PTR dwInstance, DWORD fdwOpen)
{
    if (g_BlockMicrophone) return MMSYSERR_NODRIVER; return Original_waveInOpen(phwi, uDeviceID, pwfx, dwCallback, dwInstance, fdwOpen);
}

UINT WINAPI Hooked_waveInGetNumDevs()
{
    if (g_BlockMicrophone) return 0; return Original_waveInGetNumDevs();
}

MMRESULT WINAPI Hooked_waveInGetDevCapsW(UINT_PTR uDeviceID, LPWAVEINCAPSW pwic, UINT cbwic)
{
    if (g_BlockMicrophone) return MMSYSERR_NODRIVER; return Original_waveInGetDevCapsW(uDeviceID, pwic, cbwic);
}

// ===== ХУК ActivateAudioInterfaceAsync =====
HRESULT WINAPI Hooked_ActivateAudioInterfaceAsync(LPCWSTR deviceInterfacePath, REFIID riid, PROPVARIANT* activationParams,
    IActivateAudioInterfaceCompletionHandler* completionHandler, IActivateAudioInterfaceAsyncOperation** activationOperation)
{
    if (g_BlockMicrophone)
    {
        bool isCaptureDevice = false;
        if (deviceInterfacePath != nullptr)
        {
            if (wcsstr(deviceInterfacePath, L"{0.0.1.") != nullptr) isCaptureDevice = true;
            if (wcsstr(deviceInterfacePath, L"MMDEVAPI") != nullptr) isCaptureDevice = true;
        }
        if (isCaptureDevice || IsEqualIID(riid, IID_IAudioClient) || IsEqualIID(riid, IID_IAudioClient2) ||
            IsEqualIID(riid, IID_IAudioClient3) || IsEqualIID(riid, IID_IAudioCaptureClient))
        {
            return E_ACCESSDENIED;
        }
    }
    return Original_ActivateAudioInterfaceAsync(deviceInterfacePath, riid, activationParams, completionHandler, activationOperation);
}

// 🆕 ===== НОВЫЙ ХУК: ShellExecuteExW (блокировка прав администратора) =====
BOOL WINAPI Hooked_ShellExecuteExW(LPSHELLEXECUTEINFOW pExecInfo)
{
    if (g_BlockAdminRights && pExecInfo != nullptr && pExecInfo->lpVerb != nullptr)
    {
        // "runas" — это UAC elevation (запуск от имени администратора)
        if (_wcsicmp(pExecInfo->lpVerb, L"runas") == 0)
        {
            OutputDebugStringW(L"[WinRule] >>> ShellExecuteExW BLOCKED: elevation request (runas)\n");
            if (pExecInfo->lpFile)
            {
                OutputDebugStringW(L"[WinRule]    target: ");
                OutputDebugStringW(pExecInfo->lpFile);
                OutputDebugStringW(L"\n");
            }
            SetLastError(ERROR_ACCESS_DENIED);
            return FALSE;
        }
    }
    return Original_ShellExecuteExW(pExecInfo);
}

// ===== УСТАНОВКА ХУКОВ =====
void InstallHooks()
{
    OutputDebugStringW(L"[WinRule] ========================================\n");
    OutputDebugStringW(L"[WinRule] DeviceBlocker.dll loaded!\n");
    OutputDebugStringW(L"[WinRule] ========================================\n");

    ReadSettingsFromRegistry();
    ReadBlockedPathsFromRegistry();

    HMODULE mfCore = LoadLibraryW(L"mfcore.dll");
    if (!mfCore) mfCore = GetModuleHandleW(L"mfcore.dll");

    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) return;

    if (mfCore)
    {
        void* t = GetProcAddress(mfCore, "MFEnumDeviceSources");
        if (t) MH_CreateHook(t, (void*)Hooked_MFEnumDeviceSources, (void**)&Original_MFEnumDeviceSources);
        t = GetProcAddress(mfCore, "MFCreateDeviceSource");
        if (t) MH_CreateHook(t, (void*)Hooked_MFCreateDeviceSource, (void**)&Original_MFCreateDeviceSource);
    }
    HMODULE ole32 = GetModuleHandleW(L"ole32.dll");
    if (ole32)
    {
        void* t = GetProcAddress(ole32, "CoCreateInstance");
        if (t) MH_CreateHook(t, (void*)Hooked_CoCreateInstance, (void**)&Original_CoCreateInstance);
    }
    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (kernel32)
    {
        void* t = GetProcAddress(kernel32, "CreateFileW");
        if (t) MH_CreateHook(t, (void*)Hooked_CreateFileW, (void**)&Original_CreateFileW);
        t = GetProcAddress(kernel32, "CreateFileA");
        if (t) MH_CreateHook(t, (void*)Hooked_CreateFileA, (void**)&Original_CreateFileA);
    }
    HMODULE setupapi = GetModuleHandleW(L"setupapi.dll");
    if (setupapi)
    {
        void* t = GetProcAddress(setupapi, "SetupDiGetClassDevsW");
        if (t) MH_CreateHook(t, (void*)Hooked_SetupDiGetClassDevsW, (void**)&Original_SetupDiGetClassDevsW);
    }
    HMODULE combase = GetModuleHandleW(L"combase.dll");
    if (combase)
    {
        void* t = GetProcAddress(combase, "RoActivateInstance");
        if (t) MH_CreateHook(t, (void*)Hooked_RoActivateInstance, (void**)&Original_RoActivateInstance);
    }
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (ntdll)
    {
        void* t = GetProcAddress(ntdll, "NtCreateFile");
        if (t) MH_CreateHook(t, (void*)Hooked_NtCreateFile, (void**)&Original_NtCreateFile);
        t = GetProcAddress(ntdll, "NtOpenFile");
        if (t) MH_CreateHook(t, (void*)Hooked_NtOpenFile, (void**)&Original_NtOpenFile);
    }
    HMODULE winmm = LoadLibraryW(L"winmm.dll");
    if (winmm)
    {
        void* t = GetProcAddress(winmm, "waveInOpen");
        if (t) MH_CreateHook(t, (void*)Hooked_waveInOpen, (void**)&Original_waveInOpen);
        t = GetProcAddress(winmm, "waveInGetNumDevs");
        if (t) MH_CreateHook(t, (void*)Hooked_waveInGetNumDevs, (void**)&Original_waveInGetNumDevs);
        t = GetProcAddress(winmm, "waveInGetDevCapsW");
        if (t) MH_CreateHook(t, (void*)Hooked_waveInGetDevCapsW, (void**)&Original_waveInGetDevCapsW);
    }
    HMODULE mmdevapi = LoadLibraryW(L"mmdevapi.dll");
    if (mmdevapi)
    {
        void* t = GetProcAddress(mmdevapi, "ActivateAudioInterfaceAsync");
        if (t) MH_CreateHook(t, (void*)Hooked_ActivateAudioInterfaceAsync, (void**)&Original_ActivateAudioInterfaceAsync);
    }

    // 🆕 НОВОЕ: Хук на ShellExecuteExW для блокировки админ-прав
    HMODULE shell32 = GetModuleHandleW(L"shell32.dll");
    if (shell32)
    {
        void* t = GetProcAddress(shell32, "ShellExecuteExW");
        if (t)
        {
            MH_CreateHook(t, (void*)Hooked_ShellExecuteExW, (void**)&Original_ShellExecuteExW);
            OutputDebugStringW(L"[WinRule] Hook installed: ShellExecuteExW (admin rights)\n");
        }
    }

    MH_EnableHook(MH_ALL_HOOKS);
    OutputDebugStringW(L"[WinRule] All hooks active!\n");
    OutputDebugStringW(L"[WinRule] ========================================\n");
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH: DisableThreadLibraryCalls(hModule); InstallHooks(); break;
    case DLL_PROCESS_DETACH: MH_DisableHook(MH_ALL_HOOKS); MH_Uninitialize(); FreeBlockedPaths(); break;
    }
    return TRUE;
}