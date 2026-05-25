# /platform-check — Audit code for cross-platform assumptions

Scans the codebase for platform-specific assumptions that would break on Linux or macOS, and reports findings with suggested fixes.

## Steps

1. **Scan for hardcoded path separators:**
```
Search for: \\ used as path separator (not in verbatim strings)
Search for: "/" used to join path segments manually
Flag: any string concatenation with path components
```

2. **Scan for Windows-only APIs:**
```
Search for: Registry, RegistryKey
Search for: Environment.SpecialFolder.* (some are Windows-only)
Search for: [SupportedOSPlatform] annotations that may be missing
Search for: P/Invoke to Windows DLLs (kernel32, user32, d3dcompiler)
```

3. **Scan for process execution issues:**
```
Search for: .exe hardcoded in binary names
Search for: ProcessStartInfo.Arguments (should use ArgumentList)
Search for: UseShellExecute = true (should be false)
```

4. **Scan for temp file issues:**
```
Search for: hardcoded /tmp or C:\Temp
Search for: Path.GetTempPath not used
```

5. **Scan for line ending issues:**
```
Search for: \r\n in shader source processing
Search for: string.Split('\n') without .TrimEnd('\r')
```

6. **Scan for missing Unix chmod:**
```
Search for: File.Copy or ZipFile.ExtractToDirectory for native binaries
Verify: UnixFileMode.UserExecute is set after extraction on non-Windows
```

## Report format
For each finding:
- File and line number
- The problematic code snippet
- Severity: `BREAK` (will fail on non-Windows) or `WARN` (may behave differently)
- Suggested fix

End with a summary count: `N BREAK, M WARN issues found.`
