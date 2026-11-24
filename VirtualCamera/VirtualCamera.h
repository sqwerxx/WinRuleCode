#pragma once
#include <windows.h>
#include <dshow.h>
#include <strmif.h>

extern "C" __declspec(dllexport) HRESULT RegisterVirtualCamera();
extern "C" __declspec(dllexport) HRESULT UnregisterVirtualCamera();