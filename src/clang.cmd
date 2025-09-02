@echo off
setlocal enabledelayedexpansion

@where zig >nul 2>&1
@if ERRORLEVEL 1 (
  echo Error: zig is not on the PATH. Install zig and make sure it's on PATH. Follow instructions at https://github.com/MichalStrehovsky/PublishAotCross.
  exit /B 1
)

set "args=%*"

rem Detect if this is a macOS target by looking for macOS-specific flags or target
echo !args! | findstr /C:"-apple-darwin" /C:"-framework" /C:"-exported_symbols_list" /C:"x86_64-macos" /C:"aarch64-macos" >nul
if not errorlevel 1 (
    rem We're on Windows (not macOS) and targeting macOS, so this is cross-compilation
    echo Cross-compiling to macOS target using PublishAotCross...
    
    rem Handle macOS-specific flags that zig doesn't support
    rem Remove -ld_classic flag which is macOS-specific (comprehensive removal)
    set "args= !args! "
    set "args=!args: -ld_classic = !"
    call :trim_args args
    
    rem For cross-compilation from Windows to macOS, remove ALL system libraries and frameworks
    rem Zig will handle linking to its built-in macOS system libraries automatically
    set "args= !args! "
    
    rem Remove ALL system libraries during cross-compilation
    set "args=!args: -lobjc = !"
    set "args=!args: -lz = !"
    set "args=!args: -ldl = !"
    set "args=!args: -lm = !"
    set "args=!args: -licucore = !"
    set "args=!args: -lswiftCore = !"
    set "args=!args: -lswiftFoundation = !"
    set "args=!args: -L/usr/lib/swift = !"
    
    call :trim_args args
    
    rem Remove ALL frameworks during cross-compilation
    set "args= !args! "
    
    set "args=!args: -framework CoreFoundation = !"
    set "args=!args: -framework Foundation = !"
    set "args=!args: -framework Security = !"
    set "args=!args: -framework CryptoKit = !"
    set "args=!args: -framework GSS = !"
    
    call :trim_args args
    
    rem Note: Cross-compilation removes all system libraries and frameworks
    rem Zig will link to its built-in macOS system libraries automatically
    echo Warning: Cross-compilation removes all system libraries/frameworks - zig handles linking automatically
    
) else (
    rem Linux-specific argument handling (existing logic)
    echo Cross-compiling to Linux target using PublishAotCross...
    
    rem Works around zlib not being available with zig. This is not great.
    set "args=!args:-lz =!"
    set "args=!args: -lz=!"

    rem Work around a .NET 8 Preview 6 issue
    set "args=!args:'-Wl,-rpath,$ORIGIN'=-Wl,-rpath,$ORIGIN!"

    rem Work around parameters unsupported by zig. Just drop them from the command line.
    set "args=!args:--discard-all=--as-needed!"
    
    rem Remove -pie and -Wl,-pie flags using comprehensive string replacement
    rem First add spaces to ensure proper boundary detection
    set "args= !args! "
    
    rem Remove various forms of -pie flags
    set "args=!args: -pie = !"
    set "args=!args: -Wl,-pie = !"
    
    rem Handle edge cases where -pie might be at the start or end
    if "!args:~0,5!"=="-pie " set "args=!args:~5!"
    if "!args:~0,9!"=="-Wl,-pie " set "args=!args:~9!"
    
    rem Remove trailing -pie
    if "!args:~-5!"==" -pie" set "args=!args:~0,-5!"
    if "!args:~-9!"==" -Wl,-pie" set "args=!args:~0,-9!"
    
    rem Clean up extra spaces
    call :trim_args args
    
    rem Remove other unsupported flags
    set "args=!args: -Wl,-e0x0 = !"
    set "args=!args:-Wl,-e0x0 = !"
    set "args=!args: -Wl,-e0x0=!"
    set "args=!args:-Wl,-e0x0=!"

    rem Works around zig linker dropping necessary parts of the executable.
    set "args=-Wl,-u,__Module !args!"
)

rem Run zig cc
zig cc !args!
exit /B !ERRORLEVEL!

:trim_args
rem Helper function to trim spaces from arguments and normalize spacing
rem Usage: call :trim_args variable_name
setlocal enabledelayedexpansion
set "var_name=%~1"
call set "input_args=%%%~1%%"

rem Remove leading spaces
for /f "tokens=* delims= " %%a in ("!input_args!") do set "input_args=%%a"

rem Remove trailing spaces
:trim_end
if "!input_args:~-1!"==" " (
    set "input_args=!input_args:~0,-1!"
    goto trim_end
)

rem Replace multiple spaces with single space
:normalize_spaces
set "temp_args=!input_args:  = !"
if not "!temp_args!"=="!input_args!" (
    set "input_args=!temp_args!"
    goto normalize_spaces
)

endlocal & set "%var_name%=%input_args%"
goto :EOF
