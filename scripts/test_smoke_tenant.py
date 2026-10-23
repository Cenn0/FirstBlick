import unittest

from smoke_tenant import choose_tenant


class SmokeTenantSelectionTests(unittest.TestCase):
    def setUp(self):
        self.initial = {'id': 'original', 'name': 'Initial synthetic business', 'role': 'ManagingDirector'}
        self.other = {'id': 'new', 'name': 'Another synthetic business', 'role': 'ManagingDirector'}

    def test_first_run_with_one_business(self):
        self.assertEqual('original', choose_tenant({'tenants': [self.initial]}, {},
                                                  self.initial['name'], lambda *_: self.fail('unexpected probe')))

    def test_recorded_business_wins_over_membership_order(self):
        self.assertEqual('original', choose_tenant({'tenants': [self.other, self.initial]},
                                                  {'tenantId': 'original', 'customerId': 'customer'},
                                                  self.initial['name'], lambda *_: self.fail('unexpected probe')))

    def test_older_report_proves_customer_ownership_instead_of_guessing(self):
        visited = []

        def owns(candidate, customer):
            visited.append((candidate, customer))
            return candidate == 'original'

        self.assertEqual('original', choose_tenant({'tenants': [self.other, self.initial]},
                                                  {'customerId': 'customer'}, self.initial['name'], owns))
        self.assertEqual([('new', 'customer'), ('original', 'customer')], visited)

    def test_revoked_original_business_fails_closed(self):
        with self.assertRaisesRegex(AssertionError, 'no longer accessible'):
            choose_tenant({'tenants': [self.other]}, {'tenantId': 'original'},
                          self.initial['name'], lambda *_: False)

    def test_ambiguous_name_without_recorded_evidence_fails_closed(self):
        duplicate = {**self.other, 'name': self.initial['name']}
        with self.assertRaisesRegex(AssertionError, 'ambiguous'):
            choose_tenant({'tenants': [duplicate, self.initial]}, {}, self.initial['name'], lambda *_: False)


if __name__ == '__main__':
    unittest.main()
