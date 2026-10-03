// Emscripten embind glue: slangc's own command line, in-process (issue #257).
//
// Exports the `shadowdusk-slangc` JS contract:
//     runSlangc(source: string, args: string[]) -> { exitCode: number, stdout: string, stderr: string }
//
// WHY THIS EXISTS INSTEAD OF UPSTREAM'S slang-wasm.js: upstream's official slang-wasm
// (the `slang-<ver>-wasm.zip` release asset) exposes only an embind session API whose
// `createSession(target)` takes a target and nothing else (and forces profile sm_6_6 for
// HLSL). There is no channel for `-no-mangle`, `-no-hlsl-pack-constant-buffer-elements`
// or a single `-D` macro, all of which ShadowDusk.Slang's SlangCompiler.RunSlangc passes,
// so its output cannot equal native slangc's (measured, see the Phase 67 doc). That is the
// exact "flags don't forward" defect that disqualified slang-wasm in Phase 23.
//
// This glue links UPSTREAM'S OWN prebuilt static libraries (the `slang-<ver>-wasm-libs.zip`
// release asset: libslang-compiler.a and friends, the same build upstream's slang-wasm.js
// is linked from) and replays what slangc's `innerMain` (source/slangc/main.cpp) does:
//     global session (enableGLSL = true, exactly like slangc)
//     -> spCreateCompileRequest -> setWriter(...) -> setCommandLineCompilerMode
//     -> processCommandLineArguments(args) -> compile
// so the argument list ShadowDusk builds is handed to slang's own option parser VERBATIM,
// the same way dxc-wasm-glue.cpp forwards DxcFlagBuilder's list to DXC. No flag translation.
//
// stdin: SlangCompiler pipes the source over stdin (`-- -`), and slangc names that input
// `<stdin>` in its `#line` directives and diagnostics. To keep the argument list and that
// display path identical, the source is written to a MEMFS file and `stdin` is reopened on
// it before each run; slang's own `_readStdinSource` then `fread`s it exactly as it would a
// pipe.
//
// Writers: STD_OUTPUT (the emitted HLSL) and DIAGNOSTIC/STD_ERROR are captured into strings
// by an ISlangWriter that reports isConsole() == false, i.e. what a redirected native slangc
// sees (no diagnostic colour). The managed side applies the same per-line '\n' join the
// native route's Process.OutputDataReceived produces.

#include "slang.h"
#include "slang-com-ptr.h"

#include <emscripten/bind.h>
#include <emscripten/val.h>

#include <cstdio>
#include <string>
#include <vector>

using namespace emscripten;

namespace
{
class CapturingWriter final : public ISlangWriter
{
public:
    std::string text;

    SLANG_NO_THROW SlangResult SLANG_MCALL queryInterface(SlangUUID const& uuid, void** outObject) override
    {
        if (uuid == ISlangUnknown::getTypeGuid() || uuid == ISlangWriter::getTypeGuid())
        {
            *outObject = static_cast<ISlangWriter*>(this);
            return SLANG_OK;
        }
        *outObject = nullptr;
        return SLANG_E_NO_INTERFACE;
    }
    // Stack-owned for the duration of one run; reference counting is a no-op.
    SLANG_NO_THROW uint32_t SLANG_MCALL addRef() override { return 1; }
    SLANG_NO_THROW uint32_t SLANG_MCALL release() override { return 1; }

    SLANG_NO_THROW char* SLANG_MCALL beginAppendBuffer(size_t maxNumChars) override
    {
        m_append.resize(maxNumChars);
        return m_append.data();
    }
    SLANG_NO_THROW SlangResult SLANG_MCALL endAppendBuffer(char* buffer, size_t numChars) override
    {
        text.append(buffer, numChars);
        return SLANG_OK;
    }
    SLANG_NO_THROW SlangResult SLANG_MCALL write(const char* chars, size_t numChars) override
    {
        text.append(chars, numChars);
        return SLANG_OK;
    }
    SLANG_NO_THROW void SLANG_MCALL flush() override {}
    SLANG_NO_THROW SlangBool SLANG_MCALL isConsole() override { return false; }
    SLANG_NO_THROW SlangResult SLANG_MCALL setMode(SlangWriterMode) override { return SLANG_OK; }

private:
    std::vector<char> m_append;
};

Slang::ComPtr<slang::IGlobalSession> g_session;

val makeResult(int exitCode, const std::string& out, const std::string& err)
{
    val obj = val::object();
    obj.set("exitCode", val(exitCode));
    obj.set("stdout", val(out));
    obj.set("stderr", val(err));
    return obj;
}

const char* const kStdinPath = "/tmp/shadowdusk-slangc-stdin";
} // namespace

val runSlangc(const std::string& source, const val& jsArgs)
{
    if (!g_session)
    {
        // slangc's innerMain: SlangGlobalSessionDesc{ enableGLSL = true }, non-bootstrap.
        SlangGlobalSessionDesc desc = {};
        desc.enableGLSL = true;
        if (SLANG_FAILED(slang_createGlobalSession2(&desc, g_session.writeRef())))
            return makeResult(1, "", "shadowdusk-slangc: slang_createGlobalSession2 failed\n");
    }

    FILE* f = std::fopen(kStdinPath, "wb");
    if (!f)
        return makeResult(1, "", "shadowdusk-slangc: could not stage the source for stdin\n");
    std::fwrite(source.data(), 1, source.size(), f);
    std::fclose(f);
    if (!std::freopen(kStdinPath, "rb", stdin))
        return makeResult(1, "", "shadowdusk-slangc: could not reopen stdin\n");

    std::vector<std::string> args = vecFromJSArray<std::string>(jsArgs);
    std::vector<const char*> argv;
    argv.reserve(args.size());
    for (const std::string& a : args)
        argv.push_back(a.c_str());

    CapturingWriter out;
    CapturingWriter err;

    SlangCompileRequest* request = spCreateCompileRequest(g_session);
    request->setWriter(SLANG_WRITER_CHANNEL_DIAGNOSTIC, &err);
    request->setWriter(SLANG_WRITER_CHANNEL_STD_ERROR, &err);
    request->setWriter(SLANG_WRITER_CHANNEL_STD_OUTPUT, &out);
    request->setCommandLineCompilerMode();

    SlangResult res = request->processCommandLineArguments(argv.data(), int(argv.size()));
    if (SLANG_SUCCEEDED(res))
    {
        try
        {
            res = request->compile();
        }
        catch (...)
        {
            err.text += "internal compiler error\n";
            res = SLANG_FAIL;
        }
    }
    spDestroyCompileRequest(request);

    // slangc's process exit code is TestToolUtil::getReturnCode(res); ShadowDusk only ever
    // distinguishes zero from non-zero.
    return makeResult(SLANG_SUCCEEDED(res) ? 0 : 1, out.text, err.text);
}

std::string getSlangVersion()
{
    if (!g_session)
    {
        SlangGlobalSessionDesc desc = {};
        desc.enableGLSL = true;
        if (SLANG_FAILED(slang_createGlobalSession2(&desc, g_session.writeRef())))
            return "";
    }
    return g_session->getBuildTagString();
}

EMSCRIPTEN_BINDINGS(shadowdusk_slangc)
{
    function("runSlangc", &runSlangc);
    function("getSlangVersion", &getSlangVersion);
}
