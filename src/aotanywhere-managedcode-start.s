/* AotAnywhere: managed-code range bookend, linked immediately BEFORE the ILC
   object (see _AotAnywhereComputeMacLinkArgs in DirectLink.targets).

   zig's Mach-O linker folds every code input section into __TEXT,__text
   (Atom.zig initOutputSection), so ILC's __TEXT,__managedcode collapses to a
   zero-size output section and the linker-synthesized
   section$start/end$__TEXT$__managedcode brackets - which the NativeAOT
   bootstrapper registers as the module's managed-code range - become an empty
   range. Every GC stack root-scan then fail-fasts in
   StackFrameIterator::CalculateCurrentMethodState (silent SIGABRT on the
   first collection). Defining the bracket symbols ourselves in bookend
   objects wins over the synthesis and restores a correct range.

   The bracket deliberately covers the ILC object's whole executable payload,
   including its small native __text chunk: that chunk holds only leaf thunks
   (DelegateCtor stubs, static-base helpers, reflection branch islands) that
   never contain calls, so they can never appear as a return address in a
   scanned stack frame. Windows makes the same trade - .unbox is /merge'd
   into .text inside the managed range.

   The __unbox brackets are pinned to an empty range here (all at the same
   address): zig cannot reproduce the section grouping an exact unbox range
   needs, and an empty range only degrades unboxing-stub introspection
   (RhGetCodeTarget), matching the pre-fix zig status quo.

   Directive-only, so it assembles for both osx-arm64 and osx-x64. The
   section must keep the pure_instructions attribute so the linker folds the
   markers into __text at their command-line position. */

.section __TEXT,__managedcode,regular,pure_instructions

.globl "section$start$__TEXT$__managedcode"
"section$start$__TEXT$__managedcode":

.globl "section$start$__TEXT$__unbox"
"section$start$__TEXT$__unbox":

.globl "section$end$__TEXT$__unbox"
"section$end$__TEXT$__unbox":
