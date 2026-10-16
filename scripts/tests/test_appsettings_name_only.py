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
    iter_backstop_findings,
    iter_python_listing_queries,
    iter_shell_listing_queries,
    require_exact_setting_name,
    resolve_setting_name,
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

def lookup(requested):
    for key, value in store.items():
        if key.casefold() == requested.casefold():
            return key, value
    return None, None

if query == "[].name":
    print("\\n".join(store))
elif query.endswith(".name"):
    print("\\n".join(store))
elif query.endswith("| length(@)") or query == "length(@)":
    print(len(store))
elif query.startswith("[?name=='") and query.endswith("']"):
    name = query[len("[?name=='"):-2]
    canonical, value = lookup(name)
    if canonical is not None:
        print(json.dumps([{{"name": canonical, "value": value, "slotSetting": False}}]))
    else:
        print("[]")
elif " | [0].value" in query:
    name = query.split("name=='", 1)[1].split("']", 1)[0]
    canonical, value = lookup(name)
    print("" if value is None else value)
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

    def test_get_missing_setting_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            temporary = Path(temp_dir)
            write_fake_az(temporary, {"Billing__Stripe__Enabled": "true"})
            missing = self.run_cli(
                temporary,
                "get",
                "--resource-group",
                "rg-test",
                "--name",
                "api-test",
                "--setting",
                "Billing__Stripe__SecretKey",
            )
            combined = missing.stdout + missing.stderr
            self.assertNotEqual(0, missing.returncode, combined)
            self.assertIn("not found", missing.stderr)
            self.assertEqual("", missing.stdout)
            self.assertNotIn("true", combined)

    def test_get_matches_setting_name_case_insensitively(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            temporary = Path(temp_dir)
            write_fake_az(temporary, {"Billing__Stripe__Enabled": "true"})
            result = self.run_cli(
                temporary,
                "get",
                "--resource-group",
                "rg-test",
                "--name",
                "api-test",
                "--setting",
                "billing__stripe__enabled",
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual("true\n", result.stdout)

    def test_get_empty_value_is_found(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            temporary = Path(temp_dir)
            write_fake_az(temporary, {"Billing__Stripe__WebhookSecret": ""})
            result = self.run_cli(
                temporary,
                "get",
                "--resource-group",
                "rg-test",
                "--name",
                "api-test",
                "--setting",
                "Billing__Stripe__WebhookSecret",
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual("\n", result.stdout)


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

    def test_scanner_accepts_repo_call_sites_and_rejects_bypasses(self) -> None:
        helper = ROOT / "scripts" / "lib" / "hosted-appsettings.sh"
        for query, expected in (
            ("[].name", "names"),
            ("length(@)", "names"),
            ("[?starts_with(name, 'Billing__')].name", "names"),
            (
                "[?name=='Billing__Stripe__Enabled' || name=='Billing__Lifecycle__Enabled'].name",
                "names",
            ),
            ("[?name=='Billing__Stripe__Enabled']", "exact-value"),
            ("[?name=='Billing__Stripe__Enabled'] | [0].value", "exact-value"),
            ("[?name=='Application__BuildNumber'] | [0].slotSetting", "exact-value"),
            ("[?name=='ELSA_CONTROL_IMAGE_ID'] | length(@)", "exact-value"),
        ):
            with self.subTest(query=query):
                self.assertEqual(expected, classify_appsettings_list_query(query))

        workflow = ROOT / ".github" / "workflows" / "azure-api-deploy.yml"
        staging = ROOT / "scripts" / "staging_stripe_reconcile.py"
        pairing = "[?starts_with(name, '${pairing_allowlist_prefix}')] | length(@)"
        self.assertEqual(
            "names",
            classify_appsettings_list_query(pairing, path=workflow, root=ROOT),
        )
        self.assertEqual("unsafe", classify_appsettings_list_query(pairing))
        self.assertEqual(
            "unsafe",
            classify_appsettings_list_query(
                pairing,
                path=ROOT / "scripts" / "apply-external-engine-pairing-settings.sh",
                root=ROOT,
            ),
        )
        self.assertEqual(
            "exact-value",
            classify_appsettings_list_query("[?name=='{canonical}']", path=staging, root=ROOT),
        )
        self.assertEqual(
            "exact-value",
            classify_appsettings_list_query("[?name=='{name}']", path=helper, root=ROOT),
        )
        self.assertEqual("unsafe", classify_appsettings_list_query("[?name=='{canonical}']"))
        self.assertEqual(
            "unsafe",
            classify_appsettings_list_query(
                "[?name=='{canonical}']",
                path=ROOT / "scripts" / "apply-external-engine-pairing-settings.sh",
                root=ROOT,
            ),
        )

        self.assertEqual(
            "helper",
            classify_appsettings_list_query("$query", path=helper, root=ROOT),
        )
        self.assertEqual(
            "helper",
            classify_appsettings_list_query('"$query"', path=helper, root=ROOT),
        )
        self.assertEqual("unsafe", classify_appsettings_list_query('"$query"'))
        self.assertEqual(
            "unsafe",
            classify_appsettings_list_query(
                '"$query"',
                path=ROOT / "scripts" / "apply-external-engine-pairing-settings.sh",
                root=ROOT,
            ),
        )
        self.assertEqual(
            "unsafe",
            classify_appsettings_list_query(
                "$query",
                path=ROOT / "scripts" / "vendor" / "scripts" / "lib" / "hosted-appsettings.sh",
                root=ROOT,
            ),
        )
        self.assertEqual(
            "unsafe",
            classify_appsettings_list_query(
                "$query",
                path=ROOT / ".github" / "workflows" / "scripts" / "lib" / "hosted-appsettings.sh",
                root=ROOT,
            ),
        )

        for query in (
            None,
            "[].value",
            "[?starts_with(name, 'Billing__')].value",
            "[?name=='Billing__Stripe__ExpectedMode' || name=='Billing__Stripe__Enabled'].value",
            "[?name=='Billing__Stripe__ExpectedMode' && name=='Billing__Stripe__Enabled'] | [0].value",
            "[?contains(name, 'Billing__')] | [0].value",
            "[?starts_with(name, 'Billing__')] | [0].value",
            "[?name=='Billing__Stripe__ExpectedMode' || name=='Billing__Stripe__Enabled']",
            "[].value | [].name",
            "[?name!=''] | [].{name: value} | [?name!=''].name",
            "[?starts_with(value,'x')].name",
            "[?contains(to_string(@),'x')].name",
            "[?to_string(@)!=''].name",
            "[?contains(values(@),'x')].name",
            "[?contains(@.*,'x')].name",
            "[?contains(*,'x')].name",
            r"[?starts_with('\u0076alue','x')].name",
            '[?starts_with("\\u0076alue",\'x\')].name',
            "[?contains(to_string(@),'x')] | length(@)",
        ):
            with self.subTest(bypass=query):
                self.assertEqual("unsafe", classify_appsettings_list_query(query))

        self.assertEqual(
            [None],
            list(
                iter_shell_listing_queries(
                    "az webapp config appsettings list --resource-group rg --name app --output json\n"
                    'az webapp list --query "[].name" --output tsv\n'
                )
            ),
        )
        self.assertEqual(
            [None],
            list(
                iter_shell_listing_queries(
                    "az webapp config appsettings list --resource-group rg --name app --output json && "
                    'az webapp list --query "[].name"\n'
                )
            ),
        )
        self.assertEqual(
            [None],
            list(
                iter_shell_listing_queries(
                    'x="$(az webapp config appsettings list -g a -n b -o json)"\n'
                    'y="$(az webapp list --query "[].name" -o tsv)"\n'
                )
            ),
        )
        self.assertEqual(
            [None],
            list(
                iter_shell_listing_queries(
                    "az webapp config appsettings list -g a -n b -o json # --query '[].name'\n"
                )
            ),
        )
        continued = list(
            iter_shell_listing_queries(
                'az webapp config appsettings list \\\n  --query "[].name" && echo ok\n'
            )
        )
        self.assertEqual(1, len(continued))
        self.assertEqual("names", classify_appsettings_list_query(continued[0]))
        substituted = list(
            iter_shell_listing_queries(
                'x="$(az webapp config appsettings list -g a -n b --query "[].name" -o tsv)"\n'
            )
        )
        self.assertEqual(1, len(substituted))
        self.assertEqual("names", classify_appsettings_list_query(substituted[0]))
        self.assertEqual("Billing__Stripe__Enabled", resolve_setting_name(
            "billing__stripe__enabled",
            ["Billing__Stripe__Enabled", "Billing__Stripe__SecretKey"],
        ))
        self.assertIsNone(resolve_setting_name("Missing__Setting", ["Billing__Stripe__Enabled"]))
        with self.assertRaisesRegex(Exception, "exact setting name"):
            require_exact_setting_name("Billing__*")
        with self.assertRaisesRegex(Exception, "exact setting name"):
            require_exact_setting_name("Billing__,Other")

    def test_scanner_rejects_python_and_shell_bypass_shapes(self) -> None:
        one_line = (
            'subprocess.run(["az", "webapp", "config", "appsettings", "list", '
            '"--output", "json"])\n'
        )
        self.assertEqual([None], list(iter_python_listing_queries(one_line)))

        one_line_names = (
            'subprocess.check_output(["az", "webapp", "config", "appsettings", '
            '"list", "--query", "[].name"])\n'
        )
        self.assertEqual(["[].name"], list(iter_python_listing_queries(one_line_names)))

        shell_true = (
            'subprocess.run("az webapp config appsettings list --output json", shell=True)\n'
        )
        self.assertEqual([None], list(iter_python_listing_queries(shell_true)))

        shell_true_names = (
            "subprocess.Popen(\"az webapp config appsettings list --query '[].name'\", "
            "shell=True)\n"
        )
        self.assertEqual(["[].name"], list(iter_python_listing_queries(shell_true_names)))

        unparseable = 'subprocess.run(command_for("appsettings", "list"))\n'
        self.assertEqual([None], list(iter_python_listing_queries(unparseable)))

        duplicate = (
            "az webapp config appsettings list --query '[].name' --query '[].value'\n"
        )
        self.assertEqual([None], list(iter_shell_listing_queries(duplicate)))

        quoted = "az webapp config \"appsettings\" 'list' --query '[].name'\n"
        quoted_queries = list(iter_shell_listing_queries(quoted))
        self.assertEqual(1, len(quoted_queries))
        self.assertEqual("names", classify_appsettings_list_query(quoted_queries[0]))

        self.assertEqual(
            [None],
            list(iter_shell_listing_queries("az webapp config $w list --query '[].name'\n")),
        )
        self.assertEqual(
            [None],
            list(iter_shell_listing_queries("az webapp config ${w} list --query '[].name'\n")),
        )

        for source in (
            "az webapp config appsettings list --query '[].name\n",
            "az webapp config appsettings list --query `cat query`\n",
            "eval \"az webapp config appsettings list --query '[].name'\"\n",
            'cmd="az webapp config appsettings list"\n$cmd --query \'[].name\'\n',
            "alias ll='az webapp config appsettings list'\n",
            'f() { az webapp config appsettings list "$@"; }\n',
        ):
            with self.subTest(fail_closed=source):
                queries = list(iter_shell_listing_queries(source))
                self.assertTrue(queries, source)
                self.assertTrue(
                    all(classify_appsettings_list_query(query) == "unsafe" for query in queries),
                    queries,
                )

        equals_form = list(
            iter_shell_listing_queries("az webapp config appsettings list --query='[].value'\n")
        )
        self.assertEqual(1, len(equals_form))
        self.assertEqual("unsafe", classify_appsettings_list_query(equals_form[0]))

    def test_backstop_fails_closed_on_unrecognized_appsettings(self) -> None:
        cases = (
            (
                "snippet.py",
                'cmd = "az webapp config appsettings list -o json"\n'
                "subprocess.run(cmd, shell=True)\n",
            ),
            (
                "snippet.py",
                'cmd = "az webapp config appsettings list -o json"\n'
                "subprocess.run(cmd.split())\n",
            ),
            (
                "snippet.py",
                'subprocess.run("az webapp config " + "appsettings list -o json", shell=True)\n',
            ),
            (
                "snippet.py",
                'argv = [AZ, "webapp", "config", "appsettings", "list"]\n'
                "subprocess.run(argv)\n",
            ),
            ("snippet.sh", "echo list | xargs az webapp config appsettings\n"),
            ("snippet.sh", 'f() { az webapp config appsettings list "$@"; }\n'),
        )
        for filename, source in cases:
            with self.subTest(source=source):
                findings = list(iter_backstop_findings(Path(filename), source))
                self.assertTrue(findings, source)
                self.assertTrue(all(line >= 1 and message for line, message in findings), findings)

        safe = list(
            iter_backstop_findings(
                Path("ok.sh"),
                "az webapp config appsettings set --settings @payload --output none\n"
                "az webapp config appsettings delete --setting-names X --output none\n"
                "az webapp config appsettings list --query '[].name' --output tsv\n",
            )
        )
        self.assertEqual([], safe)


class AppSettingsNameOnlyAzureWebAppTests(unittest.TestCase):
    def test_wildcard_value_reads_are_rejected(self) -> None:
        azure = AzureWebApp("rg-test", "api-test")
        with self.assertRaisesRegex(ReconciliationError, "exact setting name|rejected"):
            azure.app_setting_record("Billing__*")
        with self.assertRaisesRegex(ReconciliationError, "exact setting names"):
            azure.app_settings()

    def test_app_setting_record_matches_name_case_insensitively(self) -> None:
        observed: list[str] = []

        def runner(command: list[str], **kwargs):
            query = command[command.index("--query") + 1]

            class Result:
                returncode = 0
                stderr = ""
                stdout = (
                    "Billing__Stripe__Enabled\n"
                    if query == "[].name"
                    else '[{"name":"Billing__Stripe__Enabled","value":"true","slotSetting":false}]'
                )

            observed.append(query)
            return Result()

        record = AzureWebApp("rg-test", "api-test", runner=runner).app_setting_record(
            "billing__stripe__enabled"
        )
        self.assertIsNotNone(record)
        self.assertEqual("true", record.value)
        self.assertIn("[].name", observed)
        self.assertIn("[?name=='Billing__Stripe__Enabled']", observed)


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
