/*
 * rfcomm.c – Linux AF_BLUETOOTH raw socket for BMAP channel 2.
 * Exact equivalent of Python: socket.connect((mac, 2))
 * and Windows: SOCKADDR_BTH { port=2, serviceClassId=Guid.Empty }
 *
 * Returns a connected file descriptor (>= 0) or -errno on failure.
 * The caller wraps it in a ParcelFileDescriptor and uses FileInputStream / FileOutputStream directly.
 */
#include <jni.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <errno.h>
#include <sys/socket.h>
#include <android/log.h>

/* Linux Bluetooth socket constants (from <bluetooth/bluetooth.h>) */
#define AF_BLUETOOTH   31
#define BTPROTO_RFCOMM  3

/* bdaddr_t: 6 bytes, LSB first */
typedef struct { uint8_t b[6]; } bdaddr_t;

typedef struct {
    sa_family_t  rc_family;   /* AF_BLUETOOTH */
    bdaddr_t     rc_bdaddr;
    uint8_t      rc_channel;
} sockaddr_rc;

#define TAG "BoseCtl"

/*
 * net_bosectl_RfcommJni_connect(env, cls, macStr, channel) -> int fd
 * macStr: "AA:BB:CC:DD:EE:FF" (upper or lower case)
 * Returns fd >= 0 on success, -errno on failure.
 */
JNIEXPORT jint JNICALL
Java_net_quietrebellion_RfcommJni_connect(JNIEnv *env, jclass cls,
                                    jstring macStr, jint channel)
{
    const char *mac = (*env)->GetStringUTFChars(env, macStr, NULL);

    /* Parse "AA:BB:CC:DD:EE:FF" → bdaddr_t (LSB first) */
    unsigned int b[6];
    if (sscanf(mac, "%x:%x:%x:%x:%x:%x",
               &b[5], &b[4], &b[3], &b[2], &b[1], &b[0]) != 6) {
        (*env)->ReleaseStringUTFChars(env, macStr, mac);
        __android_log_print(ANDROID_LOG_ERROR, TAG, "rfcomm: bad MAC: %s", mac);
        return -EINVAL;
    }
    (*env)->ReleaseStringUTFChars(env, macStr, mac);

    int fd = socket(AF_BLUETOOTH, SOCK_STREAM, BTPROTO_RFCOMM);
    if (fd < 0) {
        __android_log_print(ANDROID_LOG_ERROR, TAG,
                            "rfcomm: socket() failed: %s", strerror(errno));
        return -errno;
    }

    sockaddr_rc addr;
    memset(&addr, 0, sizeof(addr));
    addr.rc_family  = AF_BLUETOOTH;
    addr.rc_channel = (uint8_t)channel;
    for (int i = 0; i < 6; i++) addr.rc_bdaddr.b[i] = (uint8_t)b[i];

    __android_log_print(ANDROID_LOG_DEBUG, TAG,
                        "rfcomm: connecting to channel %d …", channel);

    if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        int err = errno;
        __android_log_print(ANDROID_LOG_ERROR, TAG,
                            "rfcomm: connect() failed: %s", strerror(err));
        close(fd);
        return -err;
    }

    __android_log_print(ANDROID_LOG_DEBUG, TAG,
                        "rfcomm: connected, fd=%d", fd);
    return fd;
}

JNIEXPORT void JNICALL
Java_net_quietrebellion_RfcommJni_close(JNIEnv *env, jclass cls, jint fd)
{
    if (fd >= 0) close(fd);
}

