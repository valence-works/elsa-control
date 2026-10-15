#!/usr/bin/env python3
"""Name-only projection for hosted application-setting listings."""

from __future__ import annotations

import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "scripts"
LIB = SCRIPTS / "lib"
for path in (str(SCRIPTS), str(LIB)):
    if path not in sys.path:
        sys.path.insert(0, path)

from hosted_appsettings import (  # noqa: E402
    classify_appsettings_list_query,
    require_exact_setting_name,
    scan_operator_listings,
)
from staging_stripe_reconcile import AzureWebApp, ReconciliationError  # noqa: E402


CLI = ROOT / "scripts" / "hosted-appsettings.sh"
HELPER = ROOT / "scripts" / "lib" / "hosted-appsettings.sh"


def write_fake_az(temporary: Path, settings: dict[str, str]) -> Path:
    store = temporary / "settings.json"
    store.write_text(json.dumps(settings))
    call_log = temporary / "az-calls"
    fake_az = temporary / "az"
    fake_az.write_text(
        f"""#!/usr/bin/env python3
import json, os, sys
open({str(call_log)!r}, "a", encoding="utf-8").write(" ".join(sys.argv[1:]) + "\\n")
store = json.loads(open({str(store)!r}, encoding="utf-8").read())
args = sys.argv[1:]
joined = " ".join(args)
if args[:4] != ["webapp", "config", "appsettings", "list"]:
    sys.exit(1)
query = args[args.index("--query") + 1] if "--query" in args else ""
if query == "[].name":
    print("\\n".join(store))
elif query.endswith(".name"):
    prefix = "Enabled"
    print("\\n".join(name for name in store if name.endswith(prefix) or True))
elif query.endswith("| length(@)") or query == "length(@)":
    print(len(store))
elif query.startswith("[?name=='") and query.endswith("']"):
    name = query[len("[?name=='"):-2]
    if name in store:
        print(json.dumps([{{"name": name, "value": store[name], "slotSetting": False}}]))
    else:
        print("[]")
elif " | [0].value" in query:
    name = query.split("name=='", 1)[1].split("']", 1)[0]
    print(store.get(name, ""))
else:
    sys.exit(2)
"""
    )
    fake_az.chmod(0o755)
    return call_log


class AppSettingsNameOnlyCliTests(unittest.TestCase):
    def run_cli(self, temporary: Path, *args: str) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        environment["PATH"] = f"{temporary}{os.pathsep}{environment['PATH']}"
        return subprocess.run(
            [str(CLI), *args],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
            timeout=10,
        )

    def test_list_returns_names_and_never_values(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            temporary = Path(temp_dir)
            secret = "super-secret-setting-value"
            call_log = write_fake_az(
                temporary,
                {
                    "Billing__Stripe__Enabled": "true",
                    "Billing__Stripe__SecretKey": secret,
                },
            )
            result = self.run_cli(
                temporary,
                "list",
                "--resource-group",
                "rg-test",
                "--name",
                "api-test",
            )
            output = result.stdout + result.stderr
            self.assertEqual(0, result.returncode, output)
            self.assertIn("Billing__Stripe__Enabled", result.stdout)
            self.assertIn("Billing__Stripe__SecretKey", result.stdout)
            self.assertNotIn(secret, output)
            self.assertNotIn("true", result.stdout)
            self.assertIn("[].name", call_log.read_text())
            self.assertNotIn("[].value", call_log.read_text())

    def test_get_requires_one_exact_setting_name(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            temporary = Path(temp_dir)
            write_fake_az(temporary, {"Billing__Stripe__Enabled": "true"})
            accepted = self.run_cli(
                temporary,
                "get",
                "--resource-group",
                "rg-test",
                "--name",
                "api-test",
                "--setting",
                "Billing__Stripe__Enabled",
            )
            self.assertEqual(0, accepted.returncode, accepted.stdout + accepted.stderr)
            self.assertEqual("true\n", accepted.stdout)

            for rejected_args in (
                ("get", "--resource-group", "rg-test", "--name", "api-test"),
                (
                    "get",
                    "--resource-group",
                    "rg-test",
                    "--name",
                    "api-test",
                    "--setting",
                    "Billing__*",
                ),
                (
                    "get",
                    "--resource-group",
                    "rg-test",
                    "--name",
                    "api-test",
                    "--prefix",
                    "Billing__",
                    "--setting",
                    "Billing__Stripe__Enabled",
                ),
                (
                    "get",
                    "--resource-group",
                    "rg-test",
                    "--name",
                    "api-test",
                    "--setting",
                    "Billing__Stripe__Enabled,Other",
                ),
                (
                    "list",
                    "--resource-group",
                    "rg-test",
                    "--name",
                    "api-test",
                    "--query",
                    "[].value",
                ),
            ):
                with self.subTest(args=rejected_args):
                    rejected = self.run_cli(temporary, *rejected_args)
                    self.assertNotEqual(0, rejected.returncode)
                    combined = rejected.stdout + rejected.stderr
                    self.assertNotIn("true", combined)
                    self.assertTrue(
                        "exact setting name" in combined
                        or "rejected" in combined
                        or "raw queries" in combined
                    )


class AppSettingsNameOnlyScanTests(unittest.TestCase):
    def test_repo_listings_never_project_values_in_bulk(self) -> None:
        findings = scan_operator_listings(ROOT)
        self.assertTrue(findings, "expected hosted app-settings listings to scan")
        unsafe = [
            (str(path.relative_to(ROOT)), query, classification)
            for path, query, classification in findings
            if classification == "unsafe"
        ]
        self.assertEqual([], unsafe)

    def test_scanner_rejects_a_value_bearing_listing(self) -> None:
        self.assertEqual("unsafe", classify_appsettings_list_query(None))
        self.assertEqual("unsafe", classify_appsettings_list_query("[].value"))
        self.assertEqual(
            "unsafe",
            classify_appsettings_list_query("[?starts_with(name, 'Billing__')].value"),
        )
        self.assertEqual("names", classify_appsettings_list_query("[].name"))
        self.assertEqual(
            "names",
            classify_appsettings_list_query("[?starts_with(name, 'Billing__')].name"),
        )
        self.assertEqual(
            "exact-value",
            classify_appsettings_list_query("[?name=='Billing__Stripe__Enabled'] | [0].value"),
        )
        with self.assertRaisesRegex(Exception, "exact setting name"):
            require_exact_setting_name("Billing__*")
        with self.assertRaisesRegex(Exception, "exact setting name"):
            require_exact_setting_name("Billing__,Other")


class AppSettingsNameOnlyAzureWebAppTests(unittest.TestCase):
    def test_wildcard_value_reads_are_rejected(self) -> None:
        azure = AzureWebApp("rg-test", "api-test")
        with self.assertRaisesRegex(ReconciliationError, "exact setting name|rejected"):
            azure.app_setting_record("Billing__*")
        with self.assertRaisesRegex(ReconciliationError, "exact setting names"):
            azure.app_settings()


class AppSettingsNameOnlyHelperSyntaxTests(unittest.TestCase):
    def test_helper_and_cli_are_executable_shell(self) -> None:
        for path in (CLI, HELPER):
            with self.subTest(path=str(path.relative_to(ROOT))):
                checked = subprocess.run(
                    ["bash", "-n", str(path)],
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(0, checked.returncode, checked.stderr)
        self.assertTrue(stat.S_IMODE(CLI.stat().st_mode) & stat.S_IXUSR)


if __name__ == "__main__":
    unittest.main()
