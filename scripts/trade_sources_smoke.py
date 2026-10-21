"""Original XML round-trip against the actual running API and versioned S3.

Only synthetic XML and non-secret receipt identifiers enter the report. This is
not a schema-conformance test; it exercises persistence, version binding,
reviewed catalogue linkage and survival of the application restart.
"""
import base64
import hashlib
import urllib.request

GAEB = '''<GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA83/3.3">
<!-- Original synthetic comment and whitespace must survive. -->
<GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate></GAEBInfo><PrjInfo><Cur>EUR</Cur></PrjInfo>
<Award><DP>83</DP><BoQ><BoQInfo><LblBoQ>Container original source</LblBoQ></BoQInfo><BoQBody><BoQCtgy RNoPart="01"><BoQBody><Itemlist>
<Item ID="live-1" RNoPart="001"><Qty>10</Qty><QU>m²</QU><Description><CompleteText><OutlineText><OutlTxt><TextOutlTxt><span>Synthetic roof sealing</span></TextOutlTxt></OutlTxt></OutlineText></CompleteText></Description></Item>
</Itemlist></BoQBody></BoQCtgy></BoQBody></BoQ></Award></GAEB>'''
INVOICE = '''<Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2" xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
<!-- Synthetic document, not an accepted XRechnung. -->
<cbc:ID>CONTAINER-SOURCE-1</cbc:ID><cbc:IssueDate>2026-10-20</cbc:IssueDate><cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
<cac:AccountingSupplierParty><cac:Party><cac:PartyName><cbc:Name>Synthetic supplier</cbc:Name></cac:PartyName></cac:Party></cac:AccountingSupplierParty>
<cac:AccountingCustomerParty><cac:Party><cac:PartyName><cbc:Name>Synthetic buyer</cbc:Name></cac:PartyName></cac:Party></cac:AccountingCustomerParty>
<cac:LegalMonetaryTotal><cbc:TaxExclusiveAmount>50.00</cbc:TaxExclusiveAmount><cbc:TaxInclusiveAmount>59.50</cbc:TaxInclusiveAmount><cbc:PayableAmount>59.50</cbc:PayableAmount></cac:LegalMonetaryTotal>
<cac:InvoiceLine><cbc:ID>1</cbc:ID><cbc:InvoicedQuantity unitCode="C62">1</cbc:InvoicedQuantity><cbc:LineExtensionAmount>50.00</cbc:LineExtensionAmount><cac:Item><cbc:Name>Synthetic roof repair</cbc:Name></cac:Item></cac:InvoiceLine></Invoice>'''


def check_trade_sources(call, client, base, tenant, previous=None):
    previous = previous or {}
    prior = {item['format']: item for item in previous.get('sources', [])}
    sources = []
    for kind, xml in [('gaeb', GAEB), ('e-invoice', INVOICE)]:
        original = ('\ufeff' + xml.replace('\n', '\r\n') + '\r\n').encode('utf-8')
        sha = hashlib.sha256(original).hexdigest()
        encoded = base64.b64encode(original).decode('ascii')
        route = '/api/v1/integrations/v1/trade-sources/' + kind
        body = {'fileName': 'container-original.x83' if kind == 'gaeb' else 'container-invoice.xml',
                'sourceBase64': encoded, 'expectedSha256': sha}
        receipt = call('POST', route, body, tenant)
        source = receipt['source']
        assert receipt['contentVerified'] is True and source['sha256'] == sha
        assert source['size'] == len(original) and source['tenantId'] == tenant
        assert source['objectVersionId'] and source['objectVersionId'] != 'null'
        if kind in prior:
            assert receipt['alreadyStored'] is True
            for key in ['id', 'documentId', 'sha256', 'objectVersionId', 'digest']:
                assert source[key] == prior[kind][key], 'Original receipt changed after restart: ' + key
        else:
            assert receipt['alreadyStored'] is False
        duplicate = call('POST', route, body, tenant)
        assert duplicate['alreadyStored'] is True and duplicate['source'] == source
        detail = call('GET', route + '/' + source['id'], tenant=tenant)
        assert detail['contentVerified'] is True and detail['source'] == source
        assert detail['preview']['sourceSha256'] == sha
        if kind == 'gaeb':
            assert detail['preview']['externalSchemaValidationPerformed'] is False
            if kind not in prior:
                assert detail['catalogItems'] == []
                request = {'sourceBase64': encoded, 'selections': [{'positionKey': '01.001',
                    'unitMaterialCost': '12.5', 'unitLaborHours': '1', 'hourlyRate': '40',
                    'markupPercent': '10', 'vatPercent': '19', 'priceEvidence': 'Explicit synthetic container test estimates'}]}
                preview = call('POST', '/api/v1/integrations/v1/gaeb/catalog-preview', request, tenant)
                imported = call('POST', '/api/v1/integrations/v1/gaeb/catalog-import', {**request, 'previewDigest': preview['digest']}, tenant)
                assert len(imported['items']) == 1
                catalog_id = imported['items'][0]['catalogItemId']
            else:
                catalog_id = prior[kind]['catalogItemId']
            detail = call('GET', route + '/' + source['id'], tenant=tenant)
            assert len(detail['catalogItems']) == 1 and detail['catalogItems'][0]['id'] == catalog_id
            source = {**source, 'catalogItemId': catalog_id}
        else:
            assert detail['preview']['externalValidationPerformed'] is False
            assert detail['preview']['mayPostAutomatically'] is False
            assert detail['catalogItems'] == []
        request = urllib.request.Request(base + route + '/' + source['id'] + '/original',
            headers={'X-Tenant-Id': tenant, 'Accept': 'application/octet-stream'})
        with client.open(request, timeout=15) as response:
            received = response.read(1048577)
            assert received == original and hashlib.sha256(received).hexdigest() == sha
            assert response.headers.get('X-Content-SHA256') == sha
            assert response.headers.get_content_type() == 'application/octet-stream'
            assert response.headers.get('Content-Disposition', '').startswith('attachment;')
        page = call('GET', route + '?page=1&pageSize=20', tenant=tenant)
        assert page['total'] == 1 and len(page['items']) == 1 and page['contentVerified'] is False
        sources.append(source)
    invoices = call('GET', '/api/v1/supplier-invoices', tenant=tenant)
    assert invoices['total'] == 0, 'Capturing XML must not create a supplier invoice'
    return {'status': 'passed', 'sources': sources, 'preservedAcrossRestart': bool(prior),
            'checks': ['exact original bytes with UTF-8 BOM/CRLF/comments', 'concrete S3 version',
                       'tenant-bound deduplication', 'verified attachment download',
                       'reviewed GAEB catalogue source link', 'no automatic invoice posting']}
