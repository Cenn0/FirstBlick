"""Select the retained synthetic business without trusting membership order."""


def choose_tenant(identity, previous, bootstrap_name, owns_customer):
    tenants = identity['tenants']
    members = {item['id']: item for item in tenants}
    assert members and len(members) == len(tenants), 'Invalid business membership list'
    if previous.get('tenantId'):
        assert previous['tenantId'] in members, 'The original smoke business is no longer accessible'
        return previous['tenantId']
    if len(members) == 1:
        return next(iter(members))
    if previous.get('customerId'):
        matches = [candidate for candidate in members if owns_customer(candidate, previous['customerId'])]
        assert len(matches) == 1, 'The retained customer does not identify exactly one business'
        return matches[0]
    matches = [item['id'] for item in members.values()
               if item['name'] == bootstrap_name and item['role'] == 'ManagingDirector']
    assert len(matches) == 1, 'The bootstrap business is ambiguous'
    return matches[0]
