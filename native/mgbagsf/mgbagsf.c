/* mgbagsf: a small C interface over mGBA's Game Boy Advance core for PlattaPlayer's GSF codec. A GSF is a
   ROM (or multiboot) image holding a game's sound code; this boots it on mGBA with the BIOS skipped (mGBA's
   HLE BIOS serves the SWI calls), draws nothing, and hands back the audio mixer's own output samples.

   The samples come from mAVStream.postAudioFrame, one per audio tick at the rate SOUNDBIAS selects
   (32768 Hz unless the game raises the resolution, which few do). Rather than resample, a higher rate is
   averaged down to 32768 Hz, so the output rate is fixed.

   Everything that decides the coming output can be saved and restored, which the decoder uses to seek: the
   core's save state, the few samples buffered here, and the PSG channels (see struct pp_gsf_psg). */
#include <stdlib.h>
#include <string.h>

#include <mgba/core/core.h>
#include <mgba/core/interface.h>
#include <mgba/core/log.h>
#include <mgba/gba/core.h>
#include <mgba/internal/arm/isa-inlines.h>
#include <mgba/internal/gba/gba.h>
#include <mgba-util/vfs.h>

#define PP_GSF_RATE 32768
#define PP_GSF_BASE_INTERVAL 512 /* CPU cycles per sample at 32768 Hz */
#define PP_GSF_PENDING 4096      /* one video frame makes about 550 samples */
#define PP_GSF_MAX_ROM 0x02000000
#define PP_GSF_MAX_MULTIBOOT 0x00040000

/* What the wrapper itself carries between calls; saved with the core's state. */
struct pp_gsf_buffer {
	int32_t pending;            /* frames in samples, not yet handed out */
	int32_t sum_left, sum_right; /* the average being built at a raised SOUNDBIAS rate */
	int32_t summed;
	int16_t samples[PP_GSF_PENDING * 2];
};

/* mGBA 0.10.5 doesn't restore the PSG exactly from a save state: the channels' frequencies are write-only
   registers it never stores (its loader writes them back from the I/O shadow, which holds only the
   length-enable bit, so they come back as 0), and those writes also run the channels on with the
   frequencies of whatever the machine was doing before the load. A state is only ever loaded back into the
   machine that saved it, where every timestamp means the same, so the channels are simply copied as they
   were and put back after mGBA's own restore. */
struct pp_gsf_psg {
	struct GBAudioSquareChannel ch1;
	struct GBAudioSquareChannel ch2;
	struct GBAudioWaveChannel ch3;
	struct GBAudioNoiseChannel ch4;
	bool playing[4];
};

typedef struct pp_gsf {
	struct mAVStream stream; /* first, so the stream callbacks can cast back */
	struct mCore* core;
	uint8_t* image;
	size_t image_size;
	uint32_t entry;
	int multiboot;
	struct pp_gsf_buffer buffer;
} pp_gsf;

static void silent_log(struct mLogger* logger, int category, enum mLogLevel level, const char* format, va_list args) {
	(void) logger;
	(void) category;
	(void) level;
	(void) format;
	(void) args;
}

/* mGBA prints to stdout when no logger is set; the host has no console to print to. */
static struct mLogger silent_logger = { .log = silent_log };

static void post_audio_frame(struct mAVStream* stream, int16_t left, int16_t right) {
	pp_gsf* gsf = (pp_gsf*) stream;
	struct pp_gsf_buffer* b = &gsf->buffer;
	const struct GBA* gba = gsf->core->board;
	int32_t per_output = gba->audio.sampleInterval > 0 ? PP_GSF_BASE_INTERVAL / gba->audio.sampleInterval : 1;
	if (per_output < 1) {
		per_output = 1;
	}

	b->sum_left += left;
	b->sum_right += right;
	if (++b->summed < per_output) {
		return;
	}
	if (b->pending < PP_GSF_PENDING) {
		b->samples[b->pending * 2] = (int16_t) (b->sum_left / b->summed);
		b->samples[b->pending * 2 + 1] = (int16_t) (b->sum_right / b->summed);
		++b->pending;
	}
	b->sum_left = b->sum_right = b->summed = 0;
}

/* Boots the image: resets the machine (the BIOS skipped), puts a multiboot image back in EWRAM, which a reset
   doesn't reliably do, and starts the CPU at the GSF's entry point. */
static void boot(pp_gsf* gsf) {
	struct mCore* core = gsf->core;
	struct GBA* gba = core->board;
	core->reset(core);
	if (gsf->multiboot) {
		memset(gba->memory.wram, 0, SIZE_WORKING_RAM);
		memcpy(gba->memory.wram, gsf->image, gsf->image_size);
	}

	struct ARMCore* cpu = core->cpu;
	if (cpu->gprs[ARM_PC] != gsf->entry + WORD_SIZE_ARM) {
		cpu->gprs[ARM_PC] = gsf->entry;
		ARMWritePC(cpu);
	}
	memset(&gsf->buffer, 0, sizeof(gsf->buffer));
}

/* A machine running `image` (a ROM image when `entry` is in cartridge space, else a multiboot image loaded to
   EWRAM) from `entry`. The image is copied. Null when it can't be loaded. */
pp_gsf* pp_gsf_create(const uint8_t* image, size_t size, uint32_t entry) {
	int multiboot = (entry >> 24) == 2;
	if (!image || size == 0 || size > (multiboot ? PP_GSF_MAX_MULTIBOOT : PP_GSF_MAX_ROM)) {
		return NULL;
	}
	if (!multiboot && (entry >> 24) != 8 && (entry >> 24) != 9) {
		return NULL;
	}

	mLogSetDefaultLogger(&silent_logger);
	pp_gsf* gsf = calloc(1, sizeof(*gsf));
	if (!gsf) {
		return NULL;
	}
	gsf->image = malloc(size);
	gsf->core = GBACoreCreate();
	if (!gsf->image || !gsf->core || !gsf->core->init(gsf->core)) {
		free(gsf->image);
		if (gsf->core) {
			free(gsf->core);
		}
		free(gsf);
		return NULL;
	}
	memcpy(gsf->image, image, size);
	gsf->image_size = size;
	gsf->entry = entry;
	gsf->multiboot = multiboot;

	struct mCore* core = gsf->core;
	/* An empty configuration: no BIOS file is looked for, and nothing is read from the user's mGBA setup. */
	mCoreInitConfig(core, NULL);
	core->opts.useBios = false;
	core->opts.skipBios = true;
	/* A game that reads the cartridge clock gets one that starts at a fixed time and runs with the emulation,
	   so a song plays the same every time. */
	core->rtc.override = RTC_FAKE_EPOCH;
	core->rtc.value = 0;

	gsf->stream.postAudioFrame = post_audio_frame;
	core->setAVStream(core, &gsf->stream);

	struct VFile* vf = VFileMemChunk(image, size);
	struct GBA* gba = core->board;
	if (!vf || !(multiboot ? GBALoadMB(gba, vf) : GBALoadROM(gba, vf))) {
		if (vf) {
			vf->close(vf);
		}
		mCoreConfigDeinit(&core->config);
		core->deinit(core);
		free(gsf->image);
		free(gsf);
		return NULL;
	}

	boot(gsf);
	return gsf;
}

int pp_gsf_sample_rate(void) {
	return PP_GSF_RATE;
}

/* Renders `frames` stereo frames into `buffer` (interleaved; null discards them). */
void pp_gsf_render(pp_gsf* gsf, int16_t* buffer, size_t frames) {
	struct pp_gsf_buffer* b = &gsf->buffer;
	while (frames > 0) {
		if (b->pending == 0) {
			gsf->core->runFrame(gsf->core);
			/* The audio ticks with the clock whatever the game does, so a frame always makes samples; should it
			   ever not, a frame of silence keeps this from spinning forever. */
			if (b->pending == 0) {
				b->pending = PP_GSF_RATE / 60;
				memset(b->samples, 0, (size_t) b->pending * 2 * sizeof(int16_t));
			}
			continue;
		}
		size_t n = (size_t) b->pending < frames ? (size_t) b->pending : frames;
		if (buffer) {
			memcpy(buffer, b->samples, n * 2 * sizeof(int16_t));
			buffer += n * 2;
		}
		b->pending -= (int32_t) n;
		memmove(b->samples, b->samples + n * 2, (size_t) b->pending * 2 * sizeof(int16_t));
		frames -= n;
	}
}

void pp_gsf_restart(pp_gsf* gsf) {
	boot(gsf);
}

/* A saved state: the buffer, the PSG channels, then the core's own state. */
struct pp_gsf_state {
	struct pp_gsf_buffer buffer;
	struct pp_gsf_psg psg;
	uint8_t core[];
};

/* The size of a saved state (pp_gsf_save_state). */
size_t pp_gsf_state_size(pp_gsf* gsf) {
	return sizeof(struct pp_gsf_state) + gsf->core->stateSize(gsf->core);
}

/* Saves everything the coming output depends on into `state` (pp_gsf_state_size bytes). 0 on success. */
int pp_gsf_save_state(pp_gsf* gsf, void* state) {
	struct pp_gsf_state* saved = state;
	const struct GBAudio* psg = &((struct GBA*) gsf->core->board)->audio.psg;
	saved->buffer = gsf->buffer;
	saved->psg.ch1 = psg->ch1;
	saved->psg.ch2 = psg->ch2;
	saved->psg.ch3 = psg->ch3;
	saved->psg.ch4 = psg->ch4;
	saved->psg.playing[0] = psg->playingCh1;
	saved->psg.playing[1] = psg->playingCh2;
	saved->psg.playing[2] = psg->playingCh3;
	saved->psg.playing[3] = psg->playingCh4;
	return gsf->core->saveState(gsf->core, saved->core) ? 0 : -1;
}

/* Restores a state saved from this machine. 0 on success. */
int pp_gsf_load_state(pp_gsf* gsf, const void* state) {
	const struct pp_gsf_state* saved = state;
	if (!gsf->core->loadState(gsf->core, saved->core)) {
		return -1;
	}
	struct GBAudio* psg = &((struct GBA*) gsf->core->board)->audio.psg;
	psg->ch1 = saved->psg.ch1;
	psg->ch2 = saved->psg.ch2;
	psg->ch3 = saved->psg.ch3;
	psg->ch4 = saved->psg.ch4;
	psg->playingCh1 = saved->psg.playing[0];
	psg->playingCh2 = saved->psg.playing[1];
	psg->playingCh3 = saved->psg.playing[2];
	psg->playingCh4 = saved->psg.playing[3];
	gsf->buffer = saved->buffer;
	return 0;
}

void pp_gsf_destroy(pp_gsf* gsf) {
	if (!gsf) {
		return;
	}
	mCoreConfigDeinit(&gsf->core->config);
	gsf->core->deinit(gsf->core);
	free(gsf->image);
	free(gsf);
}
