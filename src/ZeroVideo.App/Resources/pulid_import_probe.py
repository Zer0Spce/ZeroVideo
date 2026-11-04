import argparse
import importlib.util
import os
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--comfy-root', required=True)
    parser.add_argument('--pulid-root', required=True)
    args = parser.parse_args()
    assert os.path.isfile(os.path.join(args.comfy_root, 'main.py'))
    assert os.path.isfile(os.path.join(args.pulid_root, '__init__.py'))
    sys.path.insert(0, args.comfy_root)
    import torch
    import insightface
    import facenet_pytorch
    import facexlib
    print('PuLID prerequisites imported', torch.__version__)


if __name__ == '__main__':
    main()
