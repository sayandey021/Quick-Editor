#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <new>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

// CLSID: {3B4D8A9F-2581-4FE4-8395-927A88C1542F}
static const GUID CLSID_QuickEditorCommand = 
{ 0x3b4d8a9f, 0x2581, 0x4fe4, { 0x83, 0x95, 0x92, 0x7a, 0x88, 0xc1, 0x54, 0x2f } };

static HINSTANCE g_hModule = NULL;
static LONG g_activeObjects = 0;

static const LPCWSTR SupportedExtensions[] = {
    L".mp4", L".mov", L".mkv", L".avi", L".wmv", L".webm", L".m4v",
    L".mp3", L".wav", L".m4a", L".aac", L".flac", L".ogg", L".wma", L".opus"
};

static bool IsSupportedExtension(LPCWSTR ext)
{
    if (!ext || *ext == 0) return false;
    for (size_t i = 0; i < sizeof(SupportedExtensions) / sizeof(SupportedExtensions[0]); ++i)
    {
        if (_wcsicmp(ext, SupportedExtensions[i]) == 0)
        {
            return true;
        }
    }
    return false;
}

static bool GetTargetExePath(LPWSTR outPath, DWORD outPathSize)
{
    WCHAR moduleDir[MAX_PATH];
    if (!GetModuleFileNameW(g_hModule, moduleDir, MAX_PATH))
    {
        return false;
    }
    PathRemoveFileSpecW(moduleDir);

    // 1. Check in the same directory
    WCHAR candidate[MAX_PATH];
    wcscpy_s(candidate, moduleDir);
    PathAppendW(candidate, L"QuickEditor.exe");
    if (PathFileExistsW(candidate))
    {
        wcscpy_s(outPath, outPathSize, candidate);
        return true;
    }

    // 2. Check parent directory
    wcscpy_s(candidate, moduleDir);
    PathRemoveFileSpecW(candidate);
    PathAppendW(candidate, L"QuickEditor.exe");
    if (PathFileExistsW(candidate))
    {
        wcscpy_s(outPath, outPathSize, candidate);
        return true;
    }

    return false;
}

class QuickEditorCommand : public IExplorerCommand
{
private:
    LONG m_refCount;

public:
    QuickEditorCommand() : m_refCount(1)
    {
        InterlockedIncrement(&g_activeObjects);
    }

    ~QuickEditorCommand()
    {
        InterlockedDecrement(&g_activeObjects);
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;

        if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef()
    {
        return InterlockedIncrement(&m_refCount);
    }

    IFACEMETHODIMP_(ULONG) Release()
    {
        LONG count = InterlockedDecrement(&m_refCount);
        if (count == 0)
        {
            delete this;
        }
        return count;
    }

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray* psiItemArray, LPWSTR* ppszName)
    {
        if (!ppszName) return E_POINTER;
        *ppszName = NULL;
        return SHStrDupW(L"Edit with Quick Editor", ppszName);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray* psiItemArray, LPWSTR* ppszIcon)
    {
        if (!ppszIcon) return E_POINTER;
        *ppszIcon = NULL;

        WCHAR exePath[MAX_PATH];
        if (GetTargetExePath(exePath, MAX_PATH))
        {
            return SHStrDupW(exePath, ppszIcon);
        }
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray* psiItemArray, LPWSTR* ppszCommands)
    {
        if (!ppszCommands) return E_POINTER;
        *ppszCommands = NULL;
        return SHStrDupW(L"Open in Quick Editor", ppszCommands);
    }

    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName)
    {
        if (!pguidCommandName) return E_POINTER;
        *pguidCommandName = CLSID_QuickEditorCommand;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* psiItemArray, BOOL fOkToBeSlow, EXPCMDSTATE* pCmdState)
    {
        if (!pCmdState) return E_POINTER;
        *pCmdState = ECS_HIDDEN;

        if (!psiItemArray) return S_OK;

        DWORD count = 0;
        if (FAILED(psiItemArray->GetCount(&count)) || count == 0)
        {
            return S_OK;
        }

        IShellItem* psi = NULL;
        if (SUCCEEDED(psiItemArray->GetItemAt(0, &psi)))
        {
            LPWSTR pszPath = NULL;
            if (SUCCEEDED(psi->GetDisplayName(SIGDN_FILESYSPATH, &pszPath)) && pszPath)
            {
                LPCWSTR ext = PathFindExtensionW(pszPath);
                if (ext && IsSupportedExtension(ext))
                {
                    *pCmdState = ECS_ENABLED;
                }
                CoTaskMemFree(pszPath);
            }
            psi->Release();
        }

        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* psiItemArray, IBindCtx* pbc)
    {
        if (!psiItemArray) return S_OK;

        DWORD count = 0;
        if (FAILED(psiItemArray->GetCount(&count)) || count == 0)
        {
            return S_OK;
        }

        WCHAR exePath[MAX_PATH];
        if (!GetTargetExePath(exePath, MAX_PATH))
        {
            return E_FAIL;
        }

        for (DWORD i = 0; i < count; ++i)
        {
            IShellItem* psi = NULL;
            if (SUCCEEDED(psiItemArray->GetItemAt(i, &psi)))
            {
                LPWSTR pszPath = NULL;
                if (SUCCEEDED(psi->GetDisplayName(SIGDN_FILESYSPATH, &pszPath)) && pszPath)
                {
                    WCHAR params[MAX_PATH * 2 + 10];
                    wsprintfW(params, L"\"%s\"", pszPath);

                    ShellExecuteW(NULL, L"open", exePath, params, NULL, SW_SHOWNORMAL);

                    CoTaskMemFree(pszPath);
                }
                psi->Release();
            }
        }

        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags)
    {
        if (!pFlags) return E_POINTER;
        *pFlags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum)
    {
        if (!ppEnum) return E_POINTER;
        *ppEnum = NULL;
        return E_NOTIMPL;
    }
};

class QuickEditorClassFactory : public IClassFactory
{
private:
    LONG m_refCount;

public:
    QuickEditorClassFactory() : m_refCount(1)
    {
        InterlockedIncrement(&g_activeObjects);
    }

    ~QuickEditorClassFactory()
    {
        InterlockedDecrement(&g_activeObjects);
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;

        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef()
    {
        return InterlockedIncrement(&m_refCount);
    }

    IFACEMETHODIMP_(ULONG) Release()
    {
        LONG count = InterlockedDecrement(&m_refCount);
        if (count == 0)
        {
            delete this;
        }
        return count;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;

        if (pUnkOuter) return CLASS_E_NOAGGREGATION;

        QuickEditorCommand* cmd = new (std::nothrow) QuickEditorCommand();
        if (!cmd) return E_OUTOFMEMORY;

        HRESULT hr = cmd->QueryInterface(riid, ppv);
        cmd->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL fLock)
    {
        if (fLock)
        {
            InterlockedIncrement(&g_activeObjects);
        }
        else
        {
            InterlockedDecrement(&g_activeObjects);
        }
        return S_OK;
    }
};

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved)
{
    if (fdwReason == DLL_PROCESS_ATTACH)
    {
        g_hModule = hinstDLL;
        DisableThreadLibraryCalls(hinstDLL);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = NULL;

    if (IsEqualCLSID(rclsid, CLSID_QuickEditorCommand))
    {
        QuickEditorClassFactory* factory = new (std::nothrow) QuickEditorClassFactory();
        if (!factory) return E_OUTOFMEMORY;

        HRESULT hr = factory->QueryInterface(riid, ppv);
        factory->Release();
        return hr;
    }

    return CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow(void)
{
    return (g_activeObjects == 0) ? S_OK : S_FALSE;
}
