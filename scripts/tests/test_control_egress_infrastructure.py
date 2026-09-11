#!/usr/bin/env python3
"""Compile and inspect the Control API static-egress template (#310)."""
import json
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MAIN = ROOT / "infra/control-egress/main.bicep"
PRODUCTION = ROOT / "infra/control-egress/main.parameters.production.json"
README = ROOT / "infra/control-egress/README.md"
ALLOWED_TYPES = {
    "Microsoft.Network/publicIPAddresses",
    "Microsoft.Network/natGateways",
    "Microsoft.Network/virtualNetworks",
}


class ControlEgressTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        az = shutil.which("az")
        if az is None:
            raise RuntimeError("Azure CLI with Bicep is required for egress contract tests")
        result = subprocess.run([az, "bicep", "build", "--file", str(MAIN), "--stdout"], capture_output=True, text=True, check=False, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.template = json.loads(result.stdout)
        cls.by_type = {}
        for resource in cls.template["resources"]:
            cls.by_type.setdefault(resource["type"], []).append(resource)
        cls.production = json.loads(PRODUCTION.read_text())["parameters"]

    def test_only_network_egress_resources_are_created(self):
        self.assertEqual(ALLOWED_TYPES, set(self.by_type))
        self.assertEqual(3, len(self.template["resources"]))
        # The VNet must share the API site's region (regional VNet integration); production Control runs
        # in Belgium Central, while West Europe is only the customer workload profile.
        self.assertEqual("belgiumcentral", self.production["location"]["value"])
        self.assertEqual("[resourceGroup().location]", self.template["parameters"]["location"]["defaultValue"])
        self.assertNotIn("allowedValues", self.template["parameters"]["location"])

    def test_public_ip_is_a_standard_static_ipv4_address(self):
        ip = self.by_type["Microsoft.Network/publicIPAddresses"][0]
        self.assertEqual({"name": "Standard", "tier": "Regional"}, ip["sku"])
        self.assertEqual("Static", ip["properties"]["publicIPAllocationMethod"])
        self.assertEqual("IPv4", ip["properties"]["publicIPAddressVersion"])
        self.assertNotIn("zones", ip)

    def test_nat_gateway_owns_exactly_the_static_ip(self):
        nat = self.by_type["Microsoft.Network/natGateways"][0]
        self.assertEqual("Standard", nat["sku"]["name"])
        addresses = nat["properties"]["publicIpAddresses"]
        self.assertEqual(1, len(addresses))
        self.assertIn("publicIPAddresses", addresses[0]["id"])
        self.assertIn("parameters('publicIpName')", addresses[0]["id"])

    def test_subnet_is_delegated_to_app_service_and_routes_through_the_nat_gateway(self):
        vnet = self.by_type["Microsoft.Network/virtualNetworks"][0]
        subnets = vnet["properties"]["subnets"]
        self.assertEqual(1, len(subnets))
        subnet = subnets[0]["properties"]
        self.assertEqual([{"name": "app-service", "properties": {"serviceName": "Microsoft.Web/serverFarms"}}], subnet["delegations"])
        self.assertIn("natGateways", subnet["natGateway"]["id"])
        self.assertIn("parameters('natGatewayName')", subnet["natGateway"]["id"])
        self.assertNotIn("networkSecurityGroup", subnet)
        self.assertNotIn("serviceEndpoints", subnet)

    def test_production_address_space_is_private_and_the_subnet_fits(self):
        vnet_prefix = self.production["virtualNetworkAddressPrefix"]["value"]
        subnet_prefix = self.production["subnetAddressPrefix"]["value"]
        self.assertTrue(vnet_prefix.startswith("10."))
        self.assertTrue(subnet_prefix.startswith(vnet_prefix.split("/")[0].rsplit(".", 1)[0]))
        self.assertLess(int(vnet_prefix.split("/")[1]), int(subnet_prefix.split("/")[1]))

    def test_outputs_expose_the_subnet_id_and_egress_address_only(self):
        self.assertEqual({"integrationSubnetResourceId", "egressIpAddress"}, set(self.template["outputs"]))

    def test_production_names_and_readme_are_consistent(self):
        readme = README.read_text()
        for name in ("virtualNetworkName", "subnetName", "natGatewayName"):
            self.assertIn(self.production[name]["value"], readme)
        self.assertIn("AZURE_API_EGRESS_SUBNET_ID", readme)
        self.assertIn("SqlBootstrapIp", readme)
        self.assertIn("vnet-integration remove", readme)
        self.assertIn("8e23037a-420f-4ad0-9594-9d194de29e84", readme)
        self.assertNotIn("a54cd7b1", readme)


if __name__ == "__main__":
    unittest.main()
