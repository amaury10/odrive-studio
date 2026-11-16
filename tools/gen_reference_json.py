#!/usr/bin/env python3
"""Régénère le JSON d'endpoints embarqué par le firmware 0.5.6 (ODrive v3.x) à partir des sources.

Utilise le générateur de référence du firmware (ref/ODrive/tools/fibre-tools/interface_generator.py)
avec `--generate-endpoints ODrive3` (ref/ODrive/Firmware/Tupfile.lua:235 et :425), exactement comme la
compilation du firmware. Le JSON obtenu est celui que la carte sert sur l'endpoint 0
(ref/ODrive/Firmware/fibre-cpp/endpoints_template.j2:31), octet pour octet.

Dépendances : pip install pyyaml jinja2 jsonschema

Usage : python tools/gen_reference_json.py [sortie.json]
"""
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIRMWARE = os.path.join(ROOT, "ref", "ODrive", "Firmware")
DEFAULT_OUTPUT = os.path.join(ROOT, "tests", "ODrive.Protocol.Tests", "data", "odrive-v3.6-0.5.6.json")


def crc16(init, data):
    """CRC16 de fibre : polynôme 0x3D65, MSB en premier (ref/ODrive/Firmware/fibre-cpp/crc.hpp:10)."""
    rem = init
    for byte in data:
        rem ^= byte << 8
        for _ in range(8):
            rem = ((rem << 1) ^ 0x3D65) if rem & 0x8000 else (rem << 1)
            rem &= 0xFFFF
    return rem


def main():
    output = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUTPUT
    with tempfile.TemporaryDirectory() as tmp:
        template = os.path.join(tmp, "json.j2")
        rendered = os.path.join(tmp, "json.txt")
        with open(template, "w", encoding="utf-8") as f:
            f.write("[[embedded_endpoint_definitions | to_c_string]]")
        subprocess.run(
            [sys.executable, "interface_generator_stub.py",
             "--definitions", "odrive-interface.yaml",
             "--generate-endpoints", "ODrive3",
             "--template", template, "--output", rendered],
            cwd=FIRMWARE, check=True)
        with open(rendered, encoding="utf-8") as f:
            lines = f.read().split("\n")

    # to_c_string : une chaîne C par ligne, guillemets échappés ; le compilateur les concatène.
    text = "".join(line[1:-1].replace('\\"', '"') for line in lines if line)
    json.loads(text)  # doit être du JSON valide
    data = text.encode("ascii")

    os.makedirs(os.path.dirname(output), exist_ok=True)
    with open(output, "wb") as f:
        f.write(data)

    # json_crc_ : CRC16 initialisé à PROTOCOL_VERSION = 1 (endpoints_template.j2:33).
    print(f"{output}: {len(data)} octets, json_crc = 0x{crc16(1, data):04X}")


if __name__ == "__main__":
    main()
