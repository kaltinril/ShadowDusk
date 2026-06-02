// emscripten --js-library: stub dlopen/dlsym/dlclose so DXC's OPTIONAL load of the
// DXIL validator (dxil.dll, via DxilLibInitialize -> DxcDllSupport::InitializeForDll
// -> dlopen) FAILS GRACEFULLY instead of trapping. We build WITHOUT the DXIL signer
// (it isn't buildable from source and the -spirv target never needs it); DXC handles a
// missing dxil.dll by setting g_DllLibResult = E_FAIL and proceeding. Without this stub,
// the default emscripten _dlopen aborts ("To use dlopen, you need enable dynamic
// linking"), killing module init before that graceful path runs.
//
// Returning 0 (NULL handle) from dlopen makes InitializeForDll fail cleanly; dlsym/
// dlclose are defensive no-ops. None of these are reached on the SPIR-V code path
// except this one optional validator probe at startup.
mergeInto(LibraryManager.library, {
  dlopen: function (_path, _flags) { return 0; },
  dlsym:  function (_handle, _sym) { return 0; },
  dlclose:function (_handle) { return 0; },
  dlerror:function () { return 0; },
});
