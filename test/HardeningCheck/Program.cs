using System;
using System.Runtime.InteropServices;

// Run natively on Windows by windows-hardening.yml.
//
//   HardeningCheck.exe            prints the live process mitigation state;
//                                 exits 0 only if the loader enabled CFG
//   HardeningCheck.exe --violate  calls `return 7` code that lives in fresh
//                                 executable memory allocated with
//                                 PAGE_TARGETS_INVALID. Without CFG the call
//                                 just works (exit 3); with CFG enforced it
//                                 is not a valid call target and the process
//                                 is killed with STATUS_STACK_BUFFER_OVERRUN
//                                 (0xC0000409, FAST_FAIL_GUARD_ICALL_CHECK_FAILURE).
unsafe
{
    if (args is ["--violate"])
    {
        // mov eax, 7; ret  /  movz w0, #7; ret
        byte[] code = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new byte[] { 0xE0, 0x00, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6 }
            : new byte[] { 0xB8, 0x07, 0x00, 0x00, 0x00, 0xC3 };
        var mem = VirtualAlloc(0, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (mem == 0)
            return 4;
        Marshal.Copy(code, 0, mem, code.Length);
        // PAGE_TARGETS_NO_UPDATE keeps the "every target invalid" CFG state
        // the allocation gets when switched to executable.
        if (VirtualProtect(mem, 4096, PAGE_EXECUTE_READ | PAGE_TARGETS_NO_UPDATE, out _) == 0
            && VirtualProtect(mem, 4096, PAGE_EXECUTE_READ, out _) == 0)
            return 5;
        FlushInstructionCache(GetCurrentProcess(), mem, (nuint)code.Length);

        var target = (delegate* unmanaged<int>)mem;
        Console.WriteLine("calling invalid target");
        Console.Out.Flush();
        var result = target();
        Console.WriteLine($"invalid indirect call was NOT stopped (returned {result})");
        return 3;
    }

    var cfg = Query(ProcessControlFlowGuardPolicy);
    var shadowStack = Query(ProcessUserShadowStackPolicy);
    var cfgEnabled = (cfg & 1) != 0;
    Console.WriteLine($"arch={RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"cfg.flags=0x{cfg:X8} EnableControlFlowGuard={cfgEnabled}");
    Console.WriteLine($"shadowstack.flags=0x{shadowStack:X8} EnableUserShadowStack={(shadowStack & 1) != 0}");
    return cfgEnabled ? 0 : 1;
}

static uint Query(int policy)
{
    uint flags = 0;
    unsafe
    {
        if (GetProcessMitigationPolicy(GetCurrentProcess(), policy, &flags, sizeof(uint)) == 0)
            return 0xFFFFFFFF;
    }
    return flags;
}

[DllImport("kernel32")]
static extern nint GetCurrentProcess();

[DllImport("kernel32")]
static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

[DllImport("kernel32")]
static extern int VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

[DllImport("kernel32")]
static extern int FlushInstructionCache(nint process, nint address, nuint size);

[DllImport("kernel32")]
static extern unsafe int GetProcessMitigationPolicy(nint process, int policy, void* buffer, nuint length);

partial class Program
{
    const int ProcessControlFlowGuardPolicy = 7;
    const int ProcessUserShadowStackPolicy = 13;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RESERVE = 0x2000;
    const uint PAGE_READWRITE = 0x04;
    const uint PAGE_EXECUTE_READ = 0x20;
    const uint PAGE_TARGETS_NO_UPDATE = 0x40000000;
}
