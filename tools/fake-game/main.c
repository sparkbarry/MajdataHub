// Native stand-in for a Unity player, used by the pipeline test and local end-to-end runs.
// Writes the RhythmHub health marker (docs/update-protocol.md §7), then idles like a running game.
// Build with -DCRASH for a player that exits immediately without reporting healthy.
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

int main(void)
{
#ifdef CRASH
    return 1;
#endif
    const char *path = getenv("RHYTHMHUB_HEALTH_FILE");
    const char *nonce = getenv("RHYTHMHUB_LAUNCH_NONCE");
    const char *version = getenv("RHYTHMHUB_GAME_VERSION");
    sleep(1);
    if (path && nonce && version)
    {
        char tmp[4096];
        snprintf(tmp, sizeof tmp, "%s.tmp", path);
        FILE *f = fopen(tmp, "w");
        if (f)
        {
            fprintf(f, "{\"nonce\":\"%s\",\"version\":\"%s\"}", nonce, version);
            fclose(f);
            rename(tmp, path);
        }
    }
    sleep(30);
    return 0;
}
