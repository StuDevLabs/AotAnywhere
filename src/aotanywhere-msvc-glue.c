/* Glue for linking MSVC-built NativeAOT runtime libs with the MinGW CRT.

   Compiled into every Windows cross-link by the AotAnywhereWindowsLink task
   (was link_shim.zig: glue_source). Supplies the MSVC CRT symbols the MinGW
   CRT lacks: /GS stack-cookie support, the MSVC on-demand TLS init scheme,
   MSVC-mangled operator new/delete, and the arm64 out-of-line _Interlocked*
   helpers. */
#include <stdlib.h>
#include <stdint.h>

/* MSVC /GS stack cookie support (normally in LIBCMT). Starts at MSVC's
   default value and is randomized by an initializer below. */
uintptr_t __security_cookie = 0x00002B992DDFA232ULL;
uintptr_t __security_cookie_complement = ~0x00002B992DDFA232ULL;

void __security_check_cookie(uintptr_t cookie)
{
    if (cookie != __security_cookie)
        __builtin_trap();
}

/* Randomize the cookie like MSVC's __security_init_cookie. Registered in
   .CRT$XCAA - the first C++-init slot, which the MinGW CRT runs via
   _initterm(__xc_a, __xc_z) before any MSVC-built initializer and before
   wmain - so no /GS-protected frame is ever live across the change.
   RtlGenRandom's exported name is SystemFunction036 (advapi32, already
   linked). MSVC keeps the top 16 bits clear on 64-bit so the cookie can
   never alias a canonical pointer. */
int __stdcall SystemFunction036(void *, unsigned long);

static void aa_init_security_cookie(void)
{
    uintptr_t cookie = 0;
    if (!SystemFunction036(&cookie, sizeof cookie)) {
        cookie = (uintptr_t)&cookie;
        cookie ^= (uintptr_t)__builtin_readcyclecounter();
    }
    cookie &= (uintptr_t)0x0000FFFFFFFFFFFFULL;
    if (cookie == 0 || cookie == 0x00002B992DDFA232ULL)
        cookie = 0x00002B992DDFA232ULL ^ (uintptr_t)&cookie;
    __security_cookie = cookie;
    __security_cookie_complement = ~cookie;
}

typedef void (*aa_initializer)(void);
__attribute__((section(".CRT$XCAA"), used))
static aa_initializer aa_init_security_cookie_entry = aa_init_security_cookie;

/* SEH personality for /GS frames, referenced from unwind data. The real one
   validates the cookie during unwind; continuing the search preserves EH
   semantics. EXCEPTION_DISPOSITION ExceptionContinueSearch == 1. */
int __GSHandlerCheck(void *rec, void *frame, void *ctx, void *disp)
{
    (void)rec; (void)frame; (void)ctx; (void)disp;
    return 1;
}

/* MSVC marker symbol pulled in by objects using floating point. */
int _fltused = 0x9875;

/* MSVC ISA dispatch level (normally set by CRT startup). 0 selects the
   baseline paths in MSVC-compiled dispatch code, which is always safe. */
int __isa_available = 0;

/* MSVC stack range check failure (emitted for large local arrays). */
__attribute__((noreturn)) void __report_rangecheckfailure(void)
{
    __builtin_trap();
}

/* MSVC 2019+ on-demand TLS init scheme (/Zc:tlsGuards). The guard is itself
   thread-local; initializing it to 1 marks TLS as "already initialized" in
   every thread, so the on-demand path never runs. The NativeAOT runtime's
   thread-locals have no dynamic initializers, so nothing is lost. */
__thread char __tls_guard = 1;

void __dyn_tls_on_demand_init(void)
{
}

#if defined(__x86_64__)
/* Control Flow Guard dispatch fallback: mingw's cfguard support object takes
   the address of this dummy, but zig's mingw bundle does not provide it.
   When no CFG-aware loader rewrites the pointer, dispatch jumps to the
   target address (x64: rax, arm64: x15). */
__attribute__((naked)) void __guard_dispatch_icall_dummy(void)
{
    __asm__("jmpq *%rax");
}
#elif defined(__aarch64__)
__attribute__((naked)) void __guard_dispatch_icall_dummy(void)
{
    __asm__("br x15");
}
#endif

#if defined(__aarch64__)
/* MSVC arm64 emits out-of-line calls for these Interlocked helpers (x64
   always inlines them). They interoperate with inlined ldaxp/stlxp
   sequences elsewhere, so they must be genuinely lock-free; on aarch64
   the __atomic builtins compile to inline exclusive-pair loops. */
long _InterlockedAnd(volatile long *p, long v) { return __atomic_fetch_and(p, v, __ATOMIC_SEQ_CST); }
long _InterlockedOr(volatile long *p, long v) { return __atomic_fetch_or(p, v, __ATOMIC_SEQ_CST); }
long _InterlockedXor(volatile long *p, long v) { return __atomic_fetch_xor(p, v, __ATOMIC_SEQ_CST); }
long _InterlockedExchange(volatile long *p, long v) { return __atomic_exchange_n(p, v, __ATOMIC_SEQ_CST); }
long _InterlockedExchangeAdd(volatile long *p, long v) { return __atomic_fetch_add(p, v, __ATOMIC_SEQ_CST); }
long _InterlockedIncrement(volatile long *p) { return __atomic_add_fetch(p, 1, __ATOMIC_SEQ_CST); }
long _InterlockedDecrement(volatile long *p) { return __atomic_sub_fetch(p, 1, __ATOMIC_SEQ_CST); }
long _InterlockedCompareExchange(volatile long *p, long exch, long cmp)
{
    __atomic_compare_exchange_n(p, &cmp, exch, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    return cmp;
}
long long _InterlockedExchange64(volatile long long *p, long long v) { return __atomic_exchange_n(p, v, __ATOMIC_SEQ_CST); }
long long _InterlockedExchangeAdd64(volatile long long *p, long long v) { return __atomic_fetch_add(p, v, __ATOMIC_SEQ_CST); }
long long _InterlockedAnd64(volatile long long *p, long long v) { return __atomic_fetch_and(p, v, __ATOMIC_SEQ_CST); }
long long _InterlockedOr64(volatile long long *p, long long v) { return __atomic_fetch_or(p, v, __ATOMIC_SEQ_CST); }
long long _InterlockedIncrement64(volatile long long *p) { return __atomic_add_fetch(p, 1, __ATOMIC_SEQ_CST); }
long long _InterlockedDecrement64(volatile long long *p) { return __atomic_sub_fetch(p, 1, __ATOMIC_SEQ_CST); }
long long _InterlockedCompareExchange64(volatile long long *p, long long exch, long long cmp)
{
    __atomic_compare_exchange_n(p, &cmp, exch, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    return cmp;
}
void *_InterlockedExchangePointer(void *volatile *p, void *v) { return __atomic_exchange_n(p, v, __ATOMIC_SEQ_CST); }
void *_InterlockedCompareExchangePointer(void *volatile *p, void *exch, void *cmp)
{
    __atomic_compare_exchange_n(p, &cmp, exch, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    return cmp;
}

unsigned char _InterlockedCompareExchange128(volatile long long *dst,
    long long exch_high, long long exch_low, long long *comparand)
{
    unsigned __int128 cmp = ((unsigned __int128)(unsigned long long)comparand[1] << 64)
                          | (unsigned long long)comparand[0];
    unsigned __int128 exch = ((unsigned __int128)(unsigned long long)exch_high << 64)
                           | (unsigned long long)exch_low;
    unsigned __int128 old = cmp;
    int ok = __atomic_compare_exchange_n((unsigned __int128 *)dst, &old, exch, 0,
                                         __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    comparand[0] = (long long)(unsigned long long)old;
    comparand[1] = (long long)(unsigned long long)(old >> 64);
    return (unsigned char)ok;
}

/* MSVC arm64 /GS prologue/epilogue helpers (crt/arm64/secpushpop.asm): the
   callee allocates a 16-byte slot holding (sp - __security_cookie) and the
   caller relies on that exact sp adjustment. x16/x17 are the only scratch
   registers safe here. */
__asm__(
    "  .text\n"
    "  .globl __security_push_cookie\n"
    "__security_push_cookie:\n"
    "  sub  sp, sp, #16\n"
    "  adrp x17, __security_cookie\n"
    "  ldr  x17, [x17, :lo12:__security_cookie]\n"
    "  sub  x17, sp, x17\n"
    "  str  x17, [sp, #8]\n"
    "  ret\n"
    "  .globl __security_pop_cookie\n"
    "__security_pop_cookie:\n"
    "  adrp x17, __security_cookie\n"
    "  ldr  x16, [sp, #8]\n"
    "  ldr  x17, [x17, :lo12:__security_cookie]\n"
    "  sub  x16, sp, x16\n"
    "  cmp  x16, x17\n"
    "  b.ne 1f\n"
    "  add  sp, sp, #16\n"
    "  ret\n"
    "1:\n"
    "  brk  #0xF001\n");
#endif

/* MSVC-mangled C++ operator new/delete and std::nothrow (normally from the
   MSVC C++ runtime), backed by the MinGW CRT heap. */
void *aa_msvc_new(size_t) __asm__("??2@YAPEAX_K@Z");
void *aa_msvc_new(size_t n) { void *p = malloc(n); if (!p) __builtin_trap(); return p; }

void *aa_msvc_new_nothrow(size_t, const void *) __asm__("??2@YAPEAX_KAEBUnothrow_t@std@@@Z");
void *aa_msvc_new_nothrow(size_t n, const void *nt) { (void)nt; return malloc(n); }

void *aa_msvc_new_arr(size_t) __asm__("??_U@YAPEAX_K@Z");
void *aa_msvc_new_arr(size_t n) { void *p = malloc(n); if (!p) __builtin_trap(); return p; }

void *aa_msvc_new_arr_nothrow(size_t, const void *) __asm__("??_U@YAPEAX_KAEBUnothrow_t@std@@@Z");
void *aa_msvc_new_arr_nothrow(size_t n, const void *nt) { (void)nt; return malloc(n); }

void aa_msvc_delete(void *) __asm__("??3@YAXPEAX@Z");
void aa_msvc_delete(void *p) { free(p); }

void aa_msvc_delete_sized(void *, size_t) __asm__("??3@YAXPEAX_K@Z");
void aa_msvc_delete_sized(void *p, size_t n) { (void)n; free(p); }

void aa_msvc_delete_arr(void *) __asm__("??_V@YAXPEAX@Z");
void aa_msvc_delete_arr(void *p) { free(p); }

void aa_msvc_delete_arr_sized(void *, size_t) __asm__("??_V@YAXPEAX_K@Z");
void aa_msvc_delete_arr_sized(void *p, size_t n) { (void)n; free(p); }

const char aa_msvc_nothrow __asm__("?nothrow@std@@3Unothrow_t@1@B") = 0;
