"""Model routing checks that do not load weights or require a GPU."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("desktop_worker", Path(__file__).resolve().parents[1] / "worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class ModelRoutingTests(unittest.TestCase):
    def test_existing_requests_default_to_turbo(self):
        trained, version, settings = worker.inference_profile({})
        self.assertIsNone(trained)
        self.assertEqual((version, settings["sample_steps"], settings["cfg_rate"]), ("v5turbo", 4, 0.0))

    def test_dev_uses_32_steps_with_cfg(self):
        trained, version, settings = worker.inference_profile({"quick_version": "v5dev"})
        self.assertIsNone(trained)
        self.assertEqual((version, settings["sample_steps"], settings["cfg_rate"]), ("v5dev", 32, 1.3))

    def test_saved_training_data_does_not_force_trained_mode(self):
        trained, version, _ = worker.inference_profile({"use_trained": False, "quick_version": "v5dev", "trained": {"version": "v4"}})
        self.assertIsNone(trained)
        self.assertEqual(version, "v5dev")

    def test_trained_model_keeps_its_version(self):
        for version in ("v2Pro", "v4"):
            with self.subTest(version=version):
                checkpoint = {"version": version, "sovits": "existing.pth", "gpt": "existing.ckpt"}
                trained, actual, settings = worker.inference_profile({"use_trained": True, "trained": checkpoint, "quick_version": "v5dev"})
                self.assertIs(trained, checkpoint)
                self.assertEqual(actual, version)
                self.assertEqual(settings["sample_steps"], 8)

    def test_missing_trained_model_does_not_silently_use_base(self):
        with self.assertRaises(ValueError):
            worker.inference_profile({"use_trained": True})

    def test_unknown_base_model_is_rejected(self):
        with self.assertRaises(ValueError):
            worker.inference_profile({"quick_version": "unknown"})

    def test_v5_is_not_presented_as_a_trained_checkpoint(self):
        with self.assertRaises(ValueError):
            worker.inference_profile({"use_trained": True, "trained": {"version": "v5dev"}})


if __name__ == "__main__":
    unittest.main()
