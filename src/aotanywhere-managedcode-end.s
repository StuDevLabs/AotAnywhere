/* AotAnywhere: managed-code range bookend, linked immediately AFTER the ILC
   object. Closes the bracket opened by aotanywhere-managedcode-start.s - see
   that file for the full story. */

.section __TEXT,__managedcode,regular,pure_instructions

.globl "section$end$__TEXT$__managedcode"
"section$end$__TEXT$__managedcode":
