/* Render entry points for PlattaPlayer. The emulated R4300 FPU switches the host's rounding mode as the game
   code asks and leaves it that way, so these restore the caller's floating-point control word before
   returning to it (the caller is a .NET audio thread that also runs other code). */
#include <float.h>

#include "usf/usf.h"

static unsigned int save_fp(void)
{
    unsigned int control = 0;
    _controlfp_s(&control, 0, 0);
    return control;
}

static void restore_fp(unsigned int control)
{
    unsigned int ignored;
    _controlfp_s(&ignored, control, _MCW_RC | _MCW_DN | _MCW_EM);
}

const char * pp_usf_render(void * state, int16_t * buffer, size_t count, int32_t * sample_rate)
{
    unsigned int control = save_fp();
    const char * error = usf_render(state, buffer, count, sample_rate);
    restore_fp(control);
    return error;
}

const char * pp_usf_render_resampled(void * state, int16_t * buffer, size_t count, int32_t sample_rate)
{
    unsigned int control = save_fp();
    const char * error = usf_render_resampled(state, buffer, count, sample_rate);
    restore_fp(control);
    return error;
}

void pp_usf_restart(void * state)
{
    unsigned int control = save_fp();
    usf_restart(state);
    restore_fp(control);
}
