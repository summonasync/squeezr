import re

DEFAULT_WIDTH = 40


class Buffer:
    MAX_LINES = 8

    def push(self, chunk):
        return len(chunk)


def normalize(value, width=DEFAULT_WIDTH):
    return re.sub(r"\s+", " ", value)[:width]


def render(row):
    return row.strip()
