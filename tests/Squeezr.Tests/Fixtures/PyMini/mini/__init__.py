'''Mini: a registry, an engine and a utils subpackage.'''

from mini.engine import Registry, build
from mini.utils import normalize

VERSION = "0.1.0"
MAX_ROWS = 64
DEFAULT = Registry()
