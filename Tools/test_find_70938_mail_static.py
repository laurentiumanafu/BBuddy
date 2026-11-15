#!/usr/bin/env python3
"""Synthetic scanner checks; no client executable is used."""
import struct
import unittest
import find_70938_mail_static as scan


class StaticMailTests(unittest.TestCase):
    def test_name_references_and_negative_displacement(self):
        data = bytearray(0x400)
        names = b'GetSendMailItem\0XSendMail\0SendMail\0'
        data[0x80:0x80+len(names)] = names
        # Data maps to RVA 0x1000; code maps to RVA 0x2000.
        source = 0x2000 + 0x20
        data[0x220:0x227] = b'\x48\x8d\x0d' + struct.pack('<i', 0x1000 - source - 7)
        # nearby lead is not silently interpreted as a handler
        source2 = source + 7
        data[0x227:0x22e] = b'\x4c\x8d\x05' + struct.pack('<i', 0x2100 - source2 - 7)
        sections = [('data',0x1000,0x80,0x80,0x40000000), ('code',0x2000,0x200,0x100,0x60000000)]
        locations, refs = scan.find_leads(bytes(data), sections, ('GetSendMailItem','SendMail'))
        self.assertEqual(locations['GetSendMailItem'], [0x1000])
        self.assertEqual(len(locations['SendMail']), 1)  # no suffix match
        self.assertEqual(refs['GetSendMailItem'][0][1:3], (source, 0x1000))
        self.assertIn((source2,0x2100), refs['GetSendMailItem'][0][3])
        self.assertEqual(refs['SendMail'], [])

    def test_nonexecutable_not_scanned(self):
        data = bytearray(64)
        data[:len(b'SendMail\0')] = b'SendMail\0'
        data[16:23] = b'\x48\x8d\x05' + struct.pack('<i', -23)
        _, refs = scan.find_leads(bytes(data), [('data',0,0,64,0x40000000)], ('SendMail',))
        self.assertEqual(refs['SendMail'], [])

    def test_invalid_pe(self):
        with self.assertRaises(ValueError): scan.sections(b'not a PE')


if __name__ == '__main__': unittest.main()
