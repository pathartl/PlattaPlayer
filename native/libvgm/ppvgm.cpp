/* ppvgm: a small C interface over libvgm's VGM player for PlattaPlayer's VGM codec. libvgm (ValleyBell) is
   the reference VGM player: it parses the log, drives emulations of the 40-odd sound chips VGM files can
   name (YM2612 and SN76489 for the Mega Drive / Genesis, YM2151, SegaPCM, RF5C164, YM2610, OPL…) and mixes
   them into one stereo stream.

   This wrapper hands back that raw mix, before libvgm's PlayerA layer: no master volume, no fade and no loop
   counting, all of which the managed decoder does itself. The mix runs on through the song's loop for as
   long as it is rendered. The managed side also undoes gzip (.vgz), so the data here is always a plain
   VGM. */
#include <stdlib.h>
#include <string.h>
#include <new>

#include "player/playerbase.hpp"
#include "player/vgmplayer.hpp"
#include "utils/DataLoader.h"
#include "emu/SoundDevs.h"
#include "emu/EmuCores.h"
#include "emu/cores/2612intf.h"

#define PP_VGM_EXPORT extern "C"

/* pp_vgm_create flags */
#define PP_VGM_NUKED_FM 0x01 /* Nuked OPN2/OPM/OPLL/OPL3 for the Yamaha FM chips they cover */

/* A DATA_LOADER over a private copy of a byte buffer. (libvgm's own MemoryLoader also inflates gzip, which
   would pull in zlib; the managed side inflates instead.) */
struct pp_mem {
	UINT8* data;
	UINT32 size;
	UINT32 pos;
};

static UINT8 mem_open(void* context) { ((pp_mem*)context)->pos = 0; return 0x00; }

static UINT32 mem_read(void* context, UINT8* buffer, UINT32 count) {
	pp_mem* mem = (pp_mem*)context;
	if (mem->pos >= mem->size) return 0;
	if (count > mem->size - mem->pos) count = mem->size - mem->pos;
	memcpy(buffer, mem->data + mem->pos, count);
	mem->pos += count;
	return count;
}

static UINT8 mem_seek(void* context, UINT32 offset, UINT8 whence) {
	pp_mem* mem = (pp_mem*)context;
	UINT32 base = whence == 1 ? mem->pos : whence == 2 ? mem->size : 0;
	mem->pos = base + offset > mem->size ? mem->size : base + offset;
	return 0x00;
}

static UINT8 mem_close(void* context) { (void)context; return 0x00; }
static INT32 mem_tell(void* context) { return (INT32)((pp_mem*)context)->pos; }
static UINT32 mem_length(void* context) { return ((pp_mem*)context)->size; }
static UINT8 mem_eof(void* context) { pp_mem* mem = (pp_mem*)context; return mem->pos >= mem->size; }

static void mem_deinit(void* context) {
	pp_mem* mem = (pp_mem*)context;
	free(mem->data);
	free(mem);
}

static const DATA_LOADER_CALLBACKS mem_callbacks = {
	0x50504D20, /* "PPM " */
	"PlattaPlayer memory",
	mem_open, mem_read, mem_seek, mem_close, mem_tell, mem_length, mem_eof, mem_deinit,
};

/* A loader holding a copy of data, already loaded; null when out of memory. */
static DATA_LOADER* mem_loader(const UINT8* data, size_t size) {
	DATA_LOADER* loader = (DATA_LOADER*)calloc(1, sizeof(DATA_LOADER));
	pp_mem* mem = (pp_mem*)calloc(1, sizeof(pp_mem));
	UINT8* copy = (UINT8*)malloc(size ? size : 1);
	if (loader == NULL || mem == NULL || copy == NULL) {
		free(loader); free(mem); free(copy);
		return NULL;
	}
	memcpy(copy, data, size);
	mem->data = copy;
	mem->size = (UINT32)size;
	DataLoader_Setup(loader, &mem_callbacks, mem);
	if (DataLoader_Load(loader)) {
		DataLoader_Deinit(loader);
		return NULL;
	}
	return loader;
}

typedef struct pp_vgm {
	VGMPlayer* player;
	DATA_LOADER* loader;
	UINT8* rom; /* the YRW801 sample ROM an OPL4 song asks for, if given */
	size_t rom_size;
	WAVE_32BS* mix;
	UINT32 mix_frames;
} pp_vgm;

/* libvgm asks for a file by name only for the OPL4's sample ROM (yrw801.rom). */
static DATA_LOADER* request_file(void* user, PlayerBase* player, const char* name) {
	pp_vgm* vgm = (pp_vgm*)user;
	(void)player;
	(void)name;
	return vgm->rom != NULL ? mem_loader(vgm->rom, vgm->rom_size) : NULL;
}

static void silent_log(void* user, PlayerBase* player, UINT8 level, UINT8 type, const char* tag, const char* message) {
	(void)user; (void)player; (void)level; (void)type; (void)tag; (void)message;
}

static void use_core(VGMPlayer* player, DEV_ID device, UINT32 core) {
	for (UINT8 instance = 0; instance < 2; instance++) {
		PLR_DEV_OPTS options;
		UINT32 id = PLR_DEV_ID(device, instance);
		if (player->GetDeviceOptions(id, options)) continue;
		options.emuCore[0] = core;
		player->SetDeviceOptions(id, options);
	}
}

PP_VGM_EXPORT void pp_vgm_destroy(pp_vgm* vgm);

/* A player for the VGM data (uncompressed; copied), started and rendering at sampleRate, or null when the
   data isn't a VGM libvgm can play. rom/romSize: the YRW801 sample ROM for OPL4 songs, or null. */
PP_VGM_EXPORT pp_vgm* pp_vgm_create(const UINT8* data, size_t size, UINT32 sample_rate, UINT32 flags,
	const UINT8* rom, size_t rom_size) {
	if (data == NULL || size < 0x40 || size > 0x7FFFFFFF || sample_rate == 0) return NULL;

	pp_vgm* vgm = (pp_vgm*)calloc(1, sizeof(pp_vgm));
	if (vgm == NULL) return NULL;
	if (rom != NULL && rom_size > 0) {
		vgm->rom = (UINT8*)malloc(rom_size);
		if (vgm->rom == NULL) { pp_vgm_destroy(vgm); return NULL; }
		memcpy(vgm->rom, rom, rom_size);
		vgm->rom_size = rom_size;
	}

	vgm->player = new (std::nothrow) VGMPlayer();
	vgm->loader = mem_loader(data, size);
	if (vgm->player == NULL || vgm->loader == NULL) { pp_vgm_destroy(vgm); return NULL; }

	VGMPlayer* player = vgm->player;
	player->SetLogCallback(silent_log, NULL);
	player->SetFileReqCallback(request_file, vgm);
	player->SetSampleRate(sample_rate);
	if (player->LoadFile(vgm->loader) >= 0x80) {
		/* Not loaded, so nothing to unload: drop the player before the loader it would otherwise touch. */
		delete vgm->player;
		vgm->player = NULL;
		pp_vgm_destroy(vgm);
		return NULL;
	}

	if (flags & PP_VGM_NUKED_FM) {
		use_core(player, DEVID_YM2612, FCC_NUKE);
		use_core(player, DEVID_YM2151, FCC_NUKE);
		use_core(player, DEVID_YM2413, FCC_NUKE);
		use_core(player, DEVID_YM3812, FCC_NUKE);
		use_core(player, DEVID_YMF262, FCC_NUKE);
	}

	if (player->Start()) { pp_vgm_destroy(vgm); return NULL; }
	return vgm;
}

/* The song's own volume adjustment (the header's volume modifier), 16.16 fixed point. */
PP_VGM_EXPORT INT32 pp_vgm_volume_gain(pp_vgm* vgm) {
	PLR_SONG_INFO info;
	return vgm->player->GetSongInfo(info) ? 0x10000 : info.volGain;
}

/* Renders frames of interleaved stereo, about 24-bit (full scale is roughly +-2^23; loud songs can exceed
   it), into buffer, or discards them when buffer is null. Returns the frames produced, fewer only when a
   song without a loop has ended. */
PP_VGM_EXPORT UINT32 pp_vgm_render(pp_vgm* vgm, INT32* buffer, UINT32 frames) {
	if (vgm->mix_frames < frames) {
		WAVE_32BS* mix = (WAVE_32BS*)realloc(vgm->mix, frames * sizeof(WAVE_32BS));
		if (mix == NULL) return 0;
		vgm->mix = mix;
		vgm->mix_frames = frames;
	}

	UINT32 done = 0;
	while (done < frames && !(vgm->player->GetState() & PLAYSTATE_END)) {
		UINT32 count = frames - done;
		memset(vgm->mix, 0, count * sizeof(WAVE_32BS));
		UINT32 rendered = vgm->player->Render(count, vgm->mix);
		if (buffer != NULL) {
			for (UINT32 i = 0; i < rendered; i++) {
				buffer[(done + i) * 2] = vgm->mix[i].L;
				buffer[(done + i) * 2 + 1] = vgm->mix[i].R;
			}
		}
		done += rendered;
		if (rendered == 0) break;
	}
	return done;
}

/* Starts the song over, as created: the chips are created afresh (VGMPlayer::Reset alone keeps their
   running state, such as oscillator phases and the resamplers' history). Stop drops the chip configurations,
   which LoadFile builds, so the file is loaded again (from the loader, already in memory); the core choices
   are player options and survive. */
PP_VGM_EXPORT void pp_vgm_restart(pp_vgm* vgm) {
	VGMPlayer* player = vgm->player;
	player->Stop();
	player->UnloadFile();
	if (player->LoadFile(vgm->loader) < 0x80) player->Start();
}

/* Moves to the frame (counted from the start, through any loops) without rendering: from a fresh start,
   libvgm replays the song's commands up to it, so the chips' registers are right but anything mid-way (a
   sound's envelope, a sample playing) starts afresh. Starting fresh makes the result depend on the frame
   alone, not on what played before. */
PP_VGM_EXPORT void pp_vgm_seek(pp_vgm* vgm, UINT32 frame) {
	pp_vgm_restart(vgm);
	if (frame > 0) vgm->player->Seek(PLAYPOS_SAMPLE, frame);
}

PP_VGM_EXPORT void pp_vgm_destroy(pp_vgm* vgm) {
	if (vgm == NULL) return;
	if (vgm->player != NULL) {
		vgm->player->Stop();
		vgm->player->UnloadFile();
		delete vgm->player;
	}
	DataLoader_Deinit(vgm->loader);
	free(vgm->rom);
	free(vgm->mix);
	free(vgm);
}
