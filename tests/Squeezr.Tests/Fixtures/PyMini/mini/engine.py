import os
import os.path
import json
import sys
from typing import Dict, List as L
from dataclasses import dataclass
from app.decorators import authenticated
import app

GLOBAL_ROWS = 0
lower_case = 1
lookup: Dict[str, int] = {}


class Shape:
    BASE = "shape"

    def sides(self):
        return 3


@dataclass
@authenticated("registry")
class Registry(Shape, metaclass=RegistryMeta):
    """Row store over a Shape."""

    MAX_ITEMS = 8

    def add(self, row):
        result = self.rows.append(row)
        CFG.MODE = "added"
        ITEMS[0] = row
        return result

    @staticmethod
    def empty():
        return Registry()

    async def drain(self, rows):
        return await render(rows)

    def label(self):
        def inner():
            return json.dumps({"n": 1})

        return os.path.join(self.BASE, inner())


@app.route("/build")
def build(rows):
    funcs = [render]
    for row in rows:
        funcs[0](row)
    total, count = len(rows), 0
    return sum(rows)


def render(row):
    return row


def text(rows):
    return len(rows)
