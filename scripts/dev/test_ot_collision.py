import unittest
import sys
from pathlib import Path
sys.path.append(str(Path(__file__).resolve().parents[1] / 're'))
from ot_components import parse, merge


class CollisionMetadataTests(unittest.TestCase):
    def test_inheritance_overrides_and_client_events_do_not_change_footprint(self):
        parent = parse('Positioned { object_size = 5 blocking = true } Animated { animated_object = "base.ao" }')
        child = parse('extends "base" Positioned { object_size = 8 } '
                      'client { Positioned { object_size = 99 } }')
        result = merge('child', {'base': parent, 'child': child}, {})
        self.assertEqual({'object_size': '8', 'blocking': 'true'}, result['positioned'])
        self.assertEqual('base.ao', result['animated_object'])

    def test_nonblocking_static_models_keep_their_model_and_override(self):
        parent = parse('Positioned { object_size = 5 blocking = true } Animated { animated_object = "base.ao" }')
        child = parse('extends "base" Positioned { blocking = false static = true } '
                      'Animated { animated_object = "bench.ao" }')
        result = merge('child', {'base': parent, 'child': child}, {})
        self.assertEqual({'object_size': '5', 'blocking': 'false', 'static': 'true'}, result['positioned'])
        self.assertEqual('bench.ao', result['animated_object'])


if __name__ == '__main__':
    unittest.main()
