import unittest
from roof_actions_smoke import verify_persisted_document


class PersistedDocumentMetadataTests(unittest.TestCase):
    def sample(self):
        return {"id": "synthetic", "createdAt": "2026-10-22T12:00:00.1234567+00:00",
            "versionId": "immutable-version", "sha256": "a" * 64, "revision": 1, "sourceDigest": "b" * 64}

    def test_initial_database_precision_conversion_is_accepted(self):
        original = self.sample()
        verify_persisted_document(original, {**original, "createdAt": "2026-10-22T12:00:00.123456Z"})

    def test_identity_version_revision_and_digests_are_never_normalized_away(self):
        for field in ("id", "versionId", "sha256", "revision", "sourceDigest"):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                verify_persisted_document(self.sample(), {**self.sample(), field: "changed"})

    def test_a_time_change_beyond_database_precision_is_rejected(self):
        with self.assertRaises(AssertionError):
            verify_persisted_document(self.sample(), {**self.sample(), "createdAt": "2026-10-22T12:00:00.123460Z"})

    def test_missing_or_extra_metadata_is_rejected(self):
        missing = self.sample()
        del missing["versionId"]
        for changed in [missing, {**self.sample(), "extra": "changed"}]:
            with self.assertRaises(AssertionError):
                verify_persisted_document(self.sample(), changed)


if __name__ == "__main__":
    unittest.main()
