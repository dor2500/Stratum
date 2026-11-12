// Stratum.ShellExt - Windows 11 context menu for Stratum.
//
// Implements IExplorerCommand: a "Stratum" entry with a submenu. Each item simply launches
// Stratum.exe (which sits next to this DLL) with the selected paths, so all real work stays
// in the app. Registered through the sparse package manifest in /packaging.

#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <wrl/module.h>
#include <algorithm>
#include <string>
#include <vector>

using namespace Microsoft::WRL;

namespace
{
    HMODULE g_module = nullptr;

    const wchar_t* const kArchiveExtensions[] = {
        L".zip", L".7z", L".rar", L".tar", L".gz", L".tgz", L".bz2", L".tbz2", L".xz", L".txz",
        L".001", L".cab", L".iso", L".wim", L".jar", L".lzh", L".arj", L".zst",
    };

    std::wstring AppPath()
    {
        wchar_t path[MAX_PATH * 2] = {};
        GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
        PathRemoveFileSpecW(path);
        return std::wstring(path) + L"\\Stratum.exe";
    }

    bool IsArchive(const std::wstring& path)
    {
        const wchar_t* ext = PathFindExtensionW(path.c_str());
        for (auto known : kArchiveExtensions)
            if (_wcsicmp(ext, known) == 0) return true;
        return false;
    }

    std::vector<std::wstring> PathsOf(IShellItemArray* items)
    {
        std::vector<std::wstring> paths;
        if (!items) return paths;
        DWORD count = 0;
        if (FAILED(items->GetCount(&count))) return paths;
        for (DWORD i = 0; i < count; i++)
        {
            ComPtr<IShellItem> item;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            PWSTR raw = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &raw)) && raw)
            {
                paths.emplace_back(raw);
                CoTaskMemFree(raw);
            }
        }
        return paths;
    }

    bool AllArchives(IShellItemArray* items)
    {
        auto paths = PathsOf(items);
        if (paths.empty()) return false;
        for (auto& p : paths)
            if (PathIsDirectoryW(p.c_str()) || !IsArchive(p)) return false;
        return true;
    }

    HRESULT Launch(const wchar_t* argument, const std::vector<std::wstring>& paths)
    {
        if (paths.empty()) return S_OK;
        auto exe = AppPath();
        std::wstring command = L"\"" + exe + L"\" " + argument;
        for (auto& p : paths)
        {
            // CreateProcess accepts at most 32767 characters; stop adding paths before that.
            if (command.size() + p.size() + 4 > 32000) break;
            command += L" \"" + p + L"\"";
        }

        STARTUPINFOW startup = { sizeof(startup) };
        PROCESS_INFORMATION process = {};
        if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
            return HRESULT_FROM_WIN32(GetLastError());
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return S_OK;
    }

    HRESULT Duplicate(const std::wstring& text, LPWSTR* out)
    {
        return SHStrDupW(text.c_str(), out);
    }
}

// One entry of the submenu.
class SubCommand : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    SubCommand(const wchar_t* title, const wchar_t* argument, bool archivesOnly, bool singleItem)
        : m_title(title), m_argument(argument), m_archivesOnly(archivesOnly), m_singleItem(singleItem) {}

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return Duplicate(m_title, name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override { *icon = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* name) override { *name = GUID_NULL; return S_OK; }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override { *commands = nullptr; return E_NOTIMPL; }

    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        bool visible = !m_archivesOnly || AllArchives(items);
        if (visible && m_singleItem)
        {
            DWORD count = 0;
            visible = items && SUCCEEDED(items->GetCount(&count)) && count == 1;
        }
        *state = visible ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        return Launch(m_argument.c_str(), PathsOf(items));
    }

private:
    std::wstring m_title;
    std::wstring m_argument;
    bool m_archivesOnly;
    bool m_singleItem;
};

class SubCommandEnumerator : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>
{
public:
    SubCommandEnumerator()
    {
        m_commands.push_back(Make<SubCommand>(L"Open with Stratum", L"--open", true, true));
        m_commands.push_back(Make<SubCommand>(L"Extract here", L"--extract-here", true, false));
        m_commands.push_back(Make<SubCommand>(L"Extract to a new folder", L"--extract-to", true, false));
        m_commands.push_back(Make<SubCommand>(L"Add to archive\x2026", L"--add", false, false));
    }

    IFACEMETHODIMP Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override
    {
        ULONG done = 0;
        while (done < count && m_index < m_commands.size())
            m_commands[m_index++].CopyTo(&commands[done++]);
        if (fetched) *fetched = done;
        return done == count ? S_OK : S_FALSE;
    }

    IFACEMETHODIMP Skip(ULONG count) override
    {
        m_index = std::min(m_index + static_cast<size_t>(count), m_commands.size());
        return S_OK;
    }

    IFACEMETHODIMP Reset() override { m_index = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** copy) override { *copy = nullptr; return E_NOTIMPL; }

private:
    std::vector<ComPtr<IExplorerCommand>> m_commands;
    size_t m_index = 0;
};

// The top-level "Stratum" entry. Its CLSID is the one named in the package manifest.
class __declspec(uuid("7B3C1E52-4A8F-4D61-9C2E-5F0A8D3B6E14")) StratumCommand
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return Duplicate(L"Stratum", name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override { return Duplicate(AppPath() + L",0", icon); }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* name) override { *name = __uuidof(StratumCommand); return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override { *state = ECS_ENABLED; return S_OK; }
    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override { return S_OK; }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_HASSUBCOMMANDS; return S_OK; }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        auto enumerator = Make<SubCommandEnumerator>();
        return enumerator ? enumerator.CopyTo(commands) : E_OUTOFMEMORY;
    }
};

CoCreatableClass(StratumCommand)

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, iid, object);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
