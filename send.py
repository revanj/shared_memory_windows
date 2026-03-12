import mmap, struct, numpy as np

WIDTH, HEIGHT, CHANNELS = 1920, 1080, 4
HEADER_SIZE = 16
DATA_SIZE = WIDTH * HEIGHT * CHANNELS

shm = mmap.mmap(-1, HEADER_SIZE + DATA_SIZE, tagname="MySharedImage")

frame_index = 0
while True:
    frame = np.random.randint(0, 255, (HEIGHT, WIDTH, CHANNELS), dtype=np.uint8)

    shm.seek(0)
    shm.write(struct.pack("iiii", WIDTH, HEIGHT, CHANNELS, frame_index))
    shm.write(frame.tobytes())

    frame_index += 1
