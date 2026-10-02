// Compiles 88lib's C interface once more with the export attribute switched on, so emu88.dll exports
// every emu88_* function. These definitions satisfy the linker before 88lib's own (non-exported) copy
// of c_interface.obj is ever pulled from the static library.
#define EMU88_SHARED
#define EMU88_EXPORTS
#include "88lib/c_interface.cpp"
