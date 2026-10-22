"""Inspect the linked ARM ELF, not a duplicate of the C descriptor macros."""
import pathlib
import struct
import sys


def symbol_data(elf, name):
    assert elf[:6] == b"\x7fELF\x01\x01", "Expected little-endian ELF32"
    section_offset = struct.unpack_from("<I", elf, 32)[0]
    section_size, section_count = struct.unpack_from("<HH", elf, 46)
    sections = [struct.unpack_from("<10I", elf, section_offset + i * section_size)
                for i in range(section_count)]
    for section in sections:
        if section[1] != 2:  # SHT_SYMTAB
            continue
        strings = sections[section[6]]
        names = elf[strings[4]:strings[4] + strings[5]]
        for offset in range(section[4], section[4] + section[5], section[9]):
            string_index, address, size, _, _, section_index = struct.unpack_from("<IIIBBH", elf, offset)
            symbol_name = names[string_index:names.index(0, string_index)].decode()
            if symbol_name == name:
                data_section = sections[section_index]
                location = data_section[4] + address - data_section[3]
                return elf[location:location + size]
    raise AssertionError(f"ELF symbol missing: {name}")


def check_config(config, notify=False):
    assert struct.unpack_from("<H", config, 2)[0] == len(config)
    assert config[4] == 4, "AC, speaker AS, microphone AS and Consumer HID"
    descriptors = []
    offset = 0
    while offset < len(config):
        length = config[offset]
        assert length >= 2 and offset + length <= len(config), "Malformed descriptor chain"
        descriptors.append(config[offset:offset + length])
        offset += length
    interfaces = [tuple(d[2:5]) for d in descriptors if d[1] == 4]
    expected_interfaces = [(0, 0, 1 if notify else 0), (1, 0, 0)] + [(1, alt, 2) for alt in range(1, 11)] + [(2,0,0)] + [(2,alt,1) for alt in range(1,10)] + [(3,0,1)]
    assert interfaces == expected_interfaces
    hid_interface=next(d for d in descriptors if d[1]==4 and d[2]==3)
    assert hid_interface[5:8]==bytes([3,0,0])
    hid_endpoint=next(d for d in descriptors if d[1]==5 and d[2]==0x83)
    assert hid_endpoint[3]==3 and int.from_bytes(hid_endpoint[4:6],"little")==2 and hid_endpoint[6]==10
    feature_units = [d for d in descriptors if d[1:3] == b"\x24\x06"]
    assert feature_units == [bytes([13, 0x24, 6, 2, 1, 2, 3, 0, 0, 0, 0, 0, 0]),bytes([11,0x24,6,7,6,2,3,0,0,0,0])], "Independent playback/microphone master controls"
    alt = 0
    formats = {}
    endpoints = {}
    for d in descriptors:
        if d[1] == 4:
            alt = d[3] if d[2] == 1 else 0
        elif alt and d[1:3] == b"\x24\x02":
            formats[alt] = d
        elif alt and d[1] == 5:
            endpoints.setdefault(alt, []).append(d)
    expected = {1: (2, 16, 48000, 196)}
    expected.update({2:(3,24,96000,582),3:(2,16,44100,184),4:(3,24,44100,276),5:(3,24,48000,294),6:(2,16,88200,360),7:(3,24,88200,540),8:(2,16,96000,388),9:(2,16,176400,712),10:(2,16,192000,772)})
    assert set(formats) == set(expected)
    for alt, (size, bits, rate, packet) in expected.items():
        f = formats[alt]
        assert f[3:8] == bytes([1, 2, size, bits, 1])
        assert int.from_bytes(f[8:11], "little") == rate
        out, feedback = endpoints[alt]
        assert out[2] == 0x01 and out[3] == 5
        assert struct.unpack_from("<H", out, 4)[0] == packet <= 1023
        assert out[6] == 1 and out[8] == 0x81
        assert feedback[2] == 0x81 and struct.unpack_from("<H", feedback, 4)[0] == 3
        assert ((rate + 999) // 1000) * size * 2 + 48 <= 1200
    mic_expected={1:(2,16,48000,98),2:(2,16,16000,34),3:(2,16,8000,18),4:(2,16,32000,66),5:(2,16,44100,92),6:(2,16,96000,194),7:(3,24,48000,147),8:(3,24,44100,138),9:(3,24,96000,291)}
    mic_formats={};mic_endpoints={};itf=alt=0
    for d in descriptors:
        if d[1]==4:itf,alt=d[2],d[3]
        elif itf==2 and alt and d[1:3]==b"\x24\x02":mic_formats[alt]=d
        elif itf==2 and alt and d[1]==5:mic_endpoints[alt]=d
    assert set(mic_formats)==set(mic_expected)
    for alt,(size,bits,rate,packet) in mic_expected.items():
        f=mic_formats[alt];ep=mic_endpoints[alt]
        assert f[3:8]==bytes([1,1,size,bits,1]) and int.from_bytes(f[8:11],"little")==rate
        assert ep[2]==0x82 and ep[3]==5 and int.from_bytes(ep[4:6],"little")==packet and ep[8]==0
        assert all(out[3]+packet+3<=1200 for out in expected.values())
    return len(config)


def check(path):
    elf = pathlib.Path(path).read_bytes()
    device = symbol_data(elf, "device_descriptor")
    assert len(device) == 18 and device[1] == 1
    assert device[4:8] == bytes([0, 0, 0, 64]), "Per-interface UAC1, EP0=64"
    assert struct.unpack_from("<HH", device, 8) == (0xCAFE, 0x401B)
    revision=struct.unpack_from("<H",device,12)[0]
    assert revision in (0x0800,0x0900)
    report=symbol_data(elf,"profile_report_template")
    assert report[:6]==bytes([0x05,0x0c,0x09,0x01,0xa1,0x01]), "Consumer Control application collection"
    assert report[8:20] == bytes([0x09,0xcd,0x09,0xb5,0x09,0xb6,0x09,0xe9,0x09,0xea,0x09,0xe2]), "six unique Consumer controls in report bit order"
    assert report[20:] == bytes([0x15,0,0x25,1,0x75,1,0x95,6,0x81,2,0x75,10,0x95,1,0x81,3,0xc0]), "six variable key bits and ten constant bits"
    template = symbol_data(elf, "configuration_template")
    size = check_config(template, revision==0x0900)
    if revision==0x0900: assert bytes([7,5,0x84,3,2,0,8]) in template, "separate two-byte AC interrupt, not HID"
    if len(sys.argv) > 2:
        pathlib.Path(sys.argv[2]).write_bytes(template)
    print(f"PASS: linked UAC1 template {size} bytes, ten stereo playback + nine mono microphone formats, OUT/feedback/IN endpoint isolation and FS payload budget")

if __name__ == "__main__":
    check(sys.argv[1])
