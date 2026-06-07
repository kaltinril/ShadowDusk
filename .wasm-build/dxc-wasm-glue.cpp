// Emscripten embind glue for the FAITHFUL DXC->WASM frontend (Phase 23 M0).
//
// Exports the `shadowdusk-dxc` JS contract:
//     compileToSpirv(hlslSource: string, args: string[]) -> { spirv: Uint8Array, error: string }
// implemented against the SAME modern DXC COM API the desktop pipeline uses
// (IDxcCompiler3 / IDxcUtils via Vortice.Dxc). The incoming `args` are the EXACT
// DxcFlagBuilder argument list ShadowDusk forwards verbatim on desktop
// (e.g. -E MainPS -T ps_5_0 -spirv -fvk-use-dx-layout -auto-binding-space 1 -Zpr -WX),
// so no flag translation happens here — DXC receives byte-for-byte the same arguments,
// which is what makes the SPIR-V byte-identical to the desktop CLI.
//
// DIAGNOSTICS CONTRACT (Phase 38): on a shader COMPILE failure this RETURNS the
// result object with an EMPTY `spirv` and `error` set to DXC's verbatim diagnostics
// (the `file:line:col: error: message` text — the SAME text the desktop reformatter
// parses). It does NOT throw for a compile error. This is deliberate: the module is
// linked with -fwasm-exceptions, so a thrown C++ exception arrives in JS as an opaque
// `WebAssembly.Exception` whose message is unreadable (`[object WebAssembly.Exception]`)
// unless the build also exports emscripten's getExceptionMessage helper (it does not).
// Returning the diagnostics as a normal value sidesteps that entirely, so a downstream
// editor (e.g. an in-browser fiddle) can surface the exact line/column. Genuine init
// failures (DxcCreateInstance, blob creation) are returned the same way. On success,
// `error` is empty and `spirv` is the compiled module.
//
// COM resolves without the Windows runtime via DXC's WinAdapter (compiled into
// libdxcompiler). C++ exceptions are enabled via -fwasm-exceptions (DXC uses them
// internally). No filesystem is needed: the source is passed in-memory as a pinned
// blob and the corpus has its #includes pre-flattened by ShadowDusk's preprocessor
// before this point (a null IDxcIncludeHandler is the desktop behaviour too).

#include "dxc/dxcapi.h"
#include <emscripten/bind.h>
#include <emscripten/val.h>

#include <string>
#include <vector>
#include <locale>
#include <codecvt>

using namespace emscripten;

// On the WinAdapter (non-Windows) build, wchar_t is 4 bytes and LPCWSTR is wchar_t*.
// DXC's arguments are passed as an array of LPCWSTR, so convert UTF-8 -> wstring.
static std::wstring Utf8ToWide(const std::string& s)
{
    std::wstring_convert<std::codecvt_utf8_utf16<wchar_t>, wchar_t> conv;
    return conv.from_bytes(s);
}

// Build a { spirv: Uint8Array, error: string } result object for the JS contract.
static val makeResult(const val& spirv, const std::string& error)
{
    val obj = val::object();
    obj.set("spirv", spirv);
    obj.set("error", val(error));
    return obj;
}

// Failure result: empty SPIR-V + the (verbatim) error/diagnostic text.
static val errorResult(const std::string& error)
{
    return makeResult(val::global("Uint8Array").new_(0), error);
}

// compileToSpirv(hlslSource, args) -> { spirv: Uint8Array, error: string }.
// Never throws for a compile/init failure — see the DIAGNOSTICS CONTRACT above.
val compileToSpirv(const std::string& hlslSource, const val& jsArgs)
{
    // Marshal the JS string[] into LPCWSTR[] (storage kept alive in `wargs`).
    const unsigned argc = jsArgs["length"].as<unsigned>();
    std::vector<std::wstring> wargs;
    wargs.reserve(argc);
    for (unsigned i = 0; i < argc; ++i)
        wargs.push_back(Utf8ToWide(jsArgs[i].as<std::string>()));

    std::vector<LPCWSTR> argv;
    argv.reserve(argc);
    for (auto& w : wargs)
        argv.push_back(w.c_str());

    IDxcUtils* utils = nullptr;
    IDxcCompiler3* compiler = nullptr;
    IDxcBlobEncoding* source = nullptr;
    IDxcResult* result = nullptr;

    auto cleanup = [&]() {
        if (result)   result->Release();
        if (source)   source->Release();
        if (compiler) compiler->Release();
        if (utils)    utils->Release();
    };

    if (FAILED(DxcCreateInstance(CLSID_DxcUtils, IID_PPV_ARGS(&utils)))) {
        cleanup();
        return errorResult("DxcCreateInstance(DxcUtils) failed");
    }
    if (FAILED(DxcCreateInstance(CLSID_DxcCompiler, IID_PPV_ARGS(&compiler)))) {
        cleanup();
        return errorResult("DxcCreateInstance(DxcCompiler) failed");
    }

    // Pin the HLSL bytes as a UTF-8 source blob (CP_UTF8). ShadowDusk hands DXC UTF-8.
    if (FAILED(utils->CreateBlobFromPinned(
            hlslSource.data(),
            static_cast<UINT32>(hlslSource.size()),
            DXC_CP_UTF8,
            &source))) {
        cleanup();
        return errorResult("IDxcUtils::CreateBlobFromPinned failed");
    }

    DxcBuffer buffer{};
    buffer.Ptr = source->GetBufferPointer();
    buffer.Size = source->GetBufferSize();
    buffer.Encoding = DXC_CP_UTF8;

    // IDxcIncludeHandler == nullptr: includes are pre-flattened upstream (desktop too).
    HRESULT hr = compiler->Compile(
        &buffer,
        argv.data(),
        static_cast<UINT32>(argv.size()),
        nullptr,
        IID_PPV_ARGS(&result));

    if (FAILED(hr) || result == nullptr) {
        cleanup();
        return errorResult("IDxcCompiler3::Compile failed (HRESULT)");
    }

    HRESULT status = E_FAIL;
    result->GetStatus(&status);

    if (FAILED(status)) {
        // Return DXC's diagnostics verbatim (same text the desktop reformatter sees).
        std::string errText = "DXC compile failed";
        IDxcBlobUtf8* errs = nullptr;
        if (SUCCEEDED(result->GetOutput(DXC_OUT_ERRORS, IID_PPV_ARGS(&errs), nullptr)) &&
            errs && errs->GetStringLength() > 0) {
            errText = std::string(errs->GetStringPointer(), errs->GetStringLength());
        }
        if (errs) errs->Release();
        cleanup();
        return errorResult(errText);
    }

    // Extract the SPIR-V object (DXC_OUT_OBJECT == the -spirv module).
    IDxcBlob* object = nullptr;
    if (FAILED(result->GetOutput(DXC_OUT_OBJECT, IID_PPV_ARGS(&object), nullptr)) ||
        object == nullptr || object->GetBufferSize() == 0) {
        if (object) object->Release();
        cleanup();
        return errorResult("DXC produced no SPIR-V object");
    }

    // Copy the bytes into a JS Uint8Array (typed_memory_view aliases WASM heap, so we
    // materialize an owned copy before releasing the blob).
    const auto* bytes = reinterpret_cast<const unsigned char*>(object->GetBufferPointer());
    const size_t n = object->GetBufferSize();

    val u8 = val::global("Uint8Array").new_(n);
    u8.call<void>("set", val(typed_memory_view(n, bytes)));

    object->Release();
    cleanup();
    return makeResult(u8, "");
}

EMSCRIPTEN_BINDINGS(shadowdusk_dxc) {
    emscripten::function("compileToSpirv", &compileToSpirv);
}
