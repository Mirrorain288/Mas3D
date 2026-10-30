"""本机桥接的访问令牌。

浏览器里的 3D 页面和 AstrBot 插件在同一个人的电脑上，但「同一台机器」不等于
「同一个信任域」：任何本机网页都能连 127.0.0.1 的端口。因此在插件数据目录里放一个
随机令牌，页面第一次打开时由玩家手动带进 URL，之后浏览器自己记住。

令牌文件路径：<plugin>/data/mas3d/token（data/ 已在 .gitignore 里）。
"""

from __future__ import annotations

import os
import secrets
from pathlib import Path

TOKEN_BYTES = 32
"""随机源长度：32 字节 = 64 个十六进制字符，足够挡住本机撞运气式的猜测。"""

MIN_TOKEN_LEN = 32
"""短于这个长度就当作文件损坏，重新生成。"""

TOKEN_DIR = "mas3d"
TOKEN_FILE = "token"


def token_path(data_dir: Path) -> Path:
    """令牌文件的位置（data_dir 是插件自己的 data/ 目录）。"""
    return Path(data_dir) / TOKEN_DIR / TOKEN_FILE


def load_token(data_dir: Path) -> str:
    """读令牌；文件不存在或内容不可用时生成一个并尽力写盘。"""
    path = token_path(data_dir)
    try:
        existing = path.read_text(encoding="utf-8").strip()
    except (FileNotFoundError, NotADirectoryError):
        existing = ""
    except OSError:
        # 权限/磁盘问题不应该让插件起不来：内存里用一个临时令牌继续跑
        existing = ""
    if len(existing) >= MIN_TOKEN_LEN:
        return existing

    token = secrets.token_hex(TOKEN_BYTES)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(token, encoding="utf-8")
        # Windows 上 chmod 只能去掉只读位，这里只是尽力而为
        os.chmod(path, 0o600)
    except OSError:
        pass
    return token


__all__ = ["MIN_TOKEN_LEN", "TOKEN_BYTES", "TOKEN_DIR", "TOKEN_FILE", "load_token", "token_path"]
