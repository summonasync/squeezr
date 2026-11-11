import mini.utils.text
import mini.engine as core
from mini.engine import Registry, build
from mini.utils.text import normalize
from . import session


class Snapshot(Registry):
    def capture(self, rows):
        return build([normalize(row) for row in rows])


def open_snapshot(rows):
    snapshot = Snapshot()
    session.load(rows)
    core.rows
    return snapshot.capture(rows)
