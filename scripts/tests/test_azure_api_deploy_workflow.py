#!/usr/bin/env python3
"""Offline contract checks for the Azure API deployment workflow."""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from textwrap import dedent


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "azure-api-deploy.yml"


class AzureApiDeployWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = WORKFLOW.read_text()

    def test_capture_supports_classic_and_sitecontainer_runtime_images(self) -> None:
        self.assertIn("image_reference_pattern=", self.source)
        self.assertIn("(@sha256:[[:xdigit:]]{64})?", self.source)
        self.assertIn(
            'if [[ "$linux_fx_version" =~ ^DOCKER\\|$image_reference_pattern$ ]]; then',
            self.source,
        )
        self.assertIn('elif [ "$linux_fx_version" = "SITECONTAINERS" ]', self.source)
        self.assertIn("az webapp sitecontainers show", self.source)
        self.assertIn("--query properties.image", self.source)
        self.assertIn(
            "Could not capture the current main sitecontainer image; refusing an unprotected deployment.",
            self.source,
        )
        self.assertIn(
            'if [[ ! "$sitecontainer_image" =~ ^$image_reference_pattern$ ]]; then',
            self.source,
        )
        self.assertNotIn("--registry-password", self.source)
        self.assertNotIn("--registry-username", self.source)

    def test_deploy_and_rollback_use_the_captured_runtime_mode(self) -> None:
        self.assertIn("env.DEPLOY_MODE != 'build'", self.source)
        self.assertIn("env.DEPLOY_MODE == 'promote'", self.source)
        self.assertIn("actions/download-artifact@v4", self.source)
        self.assertIn("actions/upload-artifact@v4", self.source)
        self.assertIn("Verify candidate image in ACR", self.source)
        self.assertIn('"::error::The validated candidate image is not present in the configured registry; refusing promotion."', self.source)
        self.assertIn('command -v jq >/dev/null 2>&1', self.source)
        self.assertIn('candidate_tag="candidate-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}-${GITHUB_SHA}"', self.source)
        self.assertIn('--name "elsa-control/api:$candidate_tag"', self.source)
        self.assertIn("candidate_descriptor_directory=\"$RUNNER_TEMP/elsa-control-api-candidate\"", self.source)
        self.assertIn('"$RUNNER_TEMP/elsa-control-api-candidate/candidate.json"', self.source)
        self.assertIn("capture_succeeded=false", self.source)
        self.assertIn("capture_succeeded=true", self.source)
        self.assertIn("steps.current-deployment.outputs.capture_succeeded == 'true'", self.source)
        main_guard_start = self.source.index("      - name: Require main ref for Azure mutation")
        restore_start = self.source.index("      - name: Restore")
        login_start = self.source.index("      - name: Log in Azure CLI")
        self.assertLess(main_guard_start, restore_start)
        self.assertLess(main_guard_start, login_start)
        main_guard = self.source[main_guard_start:restore_start]
        self.assertIn('GITHUB_REF:-', main_guard)
        self.assertNotIn("DEPLOY_MODE != 'build'", main_guard)
        self.assertIn("The promoted runtime did not match the validated immutable image", self.source)
        self.assertIn("--query linuxFxVersion", self.source)
        self.assertIn("--query properties.image", self.source)
        self.assertIn(
            'current_deployment_mode="${{ steps.current-deployment.outputs.deployment_mode }}"',
            self.source,
        )
        self.assertIn('if [ "$current_deployment_mode" = "sitecontainers" ]', self.source)
        self.assertIn('elif [ "$current_deployment_mode" = "classic" ]', self.source)
        self.assertIn("PREVIOUS_SITECONTAINER_IMAGE", self.source)
        self.assertIn('--image "$PREVIOUS_SITECONTAINER_IMAGE"', self.source)
        self.assertIn("steps.deploy-api.outcome == 'failure'", self.source)
        self.assertIn("steps.health-gate.outcome == 'failure'", self.source)
        self.assertIn(
            "Restore previous API deployment after deployment or health failure",
            self.source,
        )

    def test_staged_candidate_contract_is_immutable_and_separate_from_app_mutation(self) -> None:
        self.assertIn("candidate_run_id:", self.source)
        self.assertIn("candidate_digest:", self.source)
        self.assertIn("scripts/validate-azure-api-candidate.sh", self.source)
        self.assertIn("docker push \"$image\"", self.source)
        self.assertIn("az acr manifest show-metadata", self.source)
        self.assertIn("VALIDATED_CANDIDATE_IMAGE: ${{ steps.candidate-authority.outputs.candidate_image }}", self.source)
        self.assertIn("@sha256:", self.source)
        self.assertIn("The candidate image is not the validated immutable repository reference", self.source)
        self.assertIn("ELSA_CONTROL_IMAGE_ID", self.source)

        build_start = self.source.index("      - name: Build and publish API candidate")
        build_end = self.source.index("      - name: Upload API candidate descriptor", build_start)
        build_script = self.source[build_start:build_end]
        self.assertNotIn("az webapp", build_script)
        self.assertNotIn("restart", build_script)
        self.assertNotIn('image="$AZURE_CONTAINER_REGISTRY_ENDPOINT/elsa-control/api:$GITHUB_SHA"', build_script)
        self.assertIn('--build-arg ELSA_CONTROL_IMAGE_ID="$GITHUB_SHA"', build_script)

        promote_start = self.source.index('elif [ "$DEPLOY_MODE" = "promote" ]; then')
        promote_end = self.source.index('else\n            echo "::error::The selected deployment mode cannot mutate', promote_start)
        promote_script = self.source[promote_start:promote_end]
        self.assertNotIn("docker build", promote_script)
        self.assertNotIn("docker push", promote_script)
        self.assertIn('az webapp', promote_script)

        job_env_start = self.source.index("    env:\n", self.source.index("    permissions:"))
        job_env_end = self.source.index("    steps:\n", job_env_start)
        job_env = self.source[job_env_start:job_env_end]
        self.assertNotIn("ADMIN_API_KEY:", job_env)
        self.assertNotIn("BUILDER_CLIENT_API_KEY:", job_env)
        self.assertNotIn("SQL_ADMINISTRATOR_PASSWORD:", job_env)

        self.assertIn("env.DEPLOY_MODE == 'infra' && secrets.ADMIN_API_KEY", self.source)
        self.assertIn("env.DEPLOY_MODE == 'infra' && secrets.BUILDER_CLIENT_API_KEY", self.source)

    def test_promotion_reads_back_exact_runtime_before_settings_or_restart(self) -> None:
        deploy_start = self.source.index(
            "        run: |\n",
            self.source.index("      - name: Deploy API app"),
        )
        deploy_end = self.source.index("\n      - name:", deploy_start)
        deploy_script = dedent(self.source[deploy_start + len("        run: |\n") : deploy_end])
        deploy_script = deploy_script.replace(
            '${{ steps.current-deployment.outputs.deployment_mode }}',
            '"$CURRENT_DEPLOYMENT_MODE"',
        )
        candidate_image = "acr.azurecr.io/elsa-control/api@sha256:" + "c" * 64

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            fake_az = temp_path / "az"
            fake_az.write_text(
                """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${AZ_CALL_LOG:?}"
case "$*" in
  *"webapp sitecontainers update"*) exit 0 ;;
  *"webapp config container set"*) exit 0 ;;
  *"webapp sitecontainers show"*) printf '%s\\n' "${RUNTIME_READBACK:?}" ;;
  *"webapp config show"*) printf '%s\\n' "${RUNTIME_READBACK:?}" ;;
  *"webapp config appsettings set"*) exit 0 ;;
  *"webapp restart"*) exit 0 ;;
  *) exit 1 ;;
esac
"""
            )
            fake_az.chmod(0o755)
            call_log = temp_path / "az-calls"

            def run_promotion(
                current_deployment_mode: str,
                runtime_readback: str,
                candidate_build_number: str = "96",
            ) -> subprocess.CompletedProcess[str]:
                call_log.unlink(missing_ok=True)
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{temp_path}:{environment['PATH']}",
                        "AZ_CALL_LOG": str(call_log),
                        "AZURE_RESOURCE_GROUP": "test-rg",
                        "AZURE_WEBAPP_NAME": "test-api",
                        "AZURE_CONTAINER_REGISTRY_ENDPOINT": "acr.azurecr.io",
                        "DEPLOY_MODE": "promote",
                        "GITHUB_RUN_NUMBER": "1786839398",
                        "VALIDATED_CANDIDATE_IMAGE": candidate_image,
                        "VALIDATED_CANDIDATE_BUILD_NUMBER": candidate_build_number,
                        "CURRENT_DEPLOYMENT_MODE": current_deployment_mode,
                        "RUNTIME_READBACK": runtime_readback,
                    }
                )
                return subprocess.run(
                    ["bash", "-c", deploy_script],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=10,
                )

            classic = run_promotion("classic", f"DOCKER|{candidate_image}")
            self.assertEqual(0, classic.returncode, classic.stderr)
            classic_calls = call_log.read_text()
            self.assertIn("webapp config container set", classic_calls)
            self.assertIn("webapp config show", classic_calls)
            self.assertIn("webapp config appsettings set", classic_calls)
            self.assertIn("webapp restart", classic_calls)
            # The promoted app keeps the candidate's build number, not the promotion run number.
            self.assertIn("Application__BuildNumber=96", classic_calls)
            self.assertNotIn("Application__BuildNumber=1786839398", classic_calls)

            missing_build_number = run_promotion("classic", f"DOCKER|{candidate_image}", candidate_build_number="")
            self.assertNotEqual(0, missing_build_number.returncode)
            self.assertIn("candidate build number is unavailable", missing_build_number.stdout + missing_build_number.stderr)
            self.assertNotIn("webapp config appsettings set", call_log.read_text())

            sitecontainers = run_promotion("sitecontainers", candidate_image)
            self.assertEqual(0, sitecontainers.returncode, sitecontainers.stderr)
            sitecontainer_calls = call_log.read_text()
            self.assertIn("webapp sitecontainers update", sitecontainer_calls)
            self.assertIn("webapp sitecontainers show", sitecontainer_calls)
            self.assertIn("webapp config appsettings set", sitecontainer_calls)
            self.assertIn("webapp restart", sitecontainer_calls)

            mismatch = run_promotion(
                "classic",
                "DOCKER|acr.azurecr.io/elsa-control/api@sha256:" + "d" * 64,
            )
            self.assertNotEqual(0, mismatch.returncode)
            self.assertIn(
                "did not match the validated immutable image",
                mismatch.stdout + mismatch.stderr,
            )
            mismatch_calls = call_log.read_text()
            self.assertIn("webapp config container set", mismatch_calls)
            self.assertIn("webapp config show", mismatch_calls)
            self.assertNotIn("webapp config appsettings set", mismatch_calls)
            self.assertNotIn("webapp restart", mismatch_calls)

    def test_health_identity_separates_candidate_source_from_promotion_run(self) -> None:
        self.assertIn('VALIDATED_CANDIDATE_SOURCE_SHA: ${{ steps.candidate-authority.outputs.candidate_source_sha }}', self.source)
        self.assertIn('expected_image_id="$VALIDATED_CANDIDATE_SOURCE_SHA"', self.source)
        self.assertIn('VALIDATED_CANDIDATE_BUILD_NUMBER: ${{ steps.candidate-authority.outputs.candidate_build_number }}', self.source)
        self.assertIn('expected_build_number="$VALIDATED_CANDIDATE_BUILD_NUMBER"', self.source)
        self.assertIn('--arg expected_build_number "$expected_build_number"', self.source)
        self.assertNotIn('--arg expected_build_number "$GITHUB_RUN_NUMBER"', self.source)
        self.assertNotIn('Application__BuildNumber="$GITHUB_RUN_NUMBER"', self.source)
        self.assertIn('--arg expected_image_id "$expected_image_id"', self.source)
        self.assertIn('The Web App has a runtime image identity override; refusing promotion', self.source)

    def test_health_gates_require_exact_http_200(self) -> None:
        self.assertGreaterEqual(
            self.source.count('if [ "$http_status" = "200" ]'), 2
        )
        self.assertIn('.buildNumber == $expected_build_number', self.source)
        self.assertIn('.imageId == $expected_image_id', self.source)
        self.assertIn('expected_previous_image_id="${PREVIOUS_HEALTH_IMAGE_ID:-}"', self.source)
        self.assertIn("PREVIOUS_HEALTH_IMAGE_ID", self.source)
        self.assertIn("PREVIOUS_HEALTH_BUILD_NUMBER", self.source)
        self.assertIn("previous_health_image_id=\"$(jq -r '.imageId // empty'", self.source)
        self.assertIn('restored_runtime_image=', self.source)
        self.assertIn(
            "expected_previous_health_query='.status == \"ok\" and .buildNumber == $expected_build_number and .imageId == $expected_image_id'",
            self.source,
        )
        self.assertIn('legacy-image compatibility path', self.source)
        self.assertIn(
            'Azure is not configured with the captured previous main sitecontainer image',
            self.source,
        )
        self.assertIn('if [ "$stable_health_probes" -ge 2 ]; then', self.source)
        self.assertIn('if [ "$stable_rollback_health_probes" -ge 2 ]; then', self.source)

    def test_infra_deploy_uses_the_checked_in_api_dockerfile(self) -> None:
        deploy_script = (ROOT / "scripts" / "deploy-azure-elsa-control.sh").read_text()
        self.assertIn("--build-arg ELSA_CONTROL_IMAGE_ID=", deploy_script)
        self.assertIn("--file src/Hosting/ElsaControl.Api/Dockerfile", deploy_script)
        self.assertNotIn("--file src/ElsaControl.Api/Dockerfile", deploy_script)

    def test_capture_rejects_credential_bearing_and_scheme_based_images(self) -> None:
        capture_start = self.source.index(
            "        run: |\n",
            self.source.index("      - name: Capture current API deployment"),
        )
        capture_end = self.source.index("\n      - name:", capture_start)
        capture_script = dedent(self.source[capture_start + len("        run: |\n") : capture_end])

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            fake_az = temp_path / "az"
            fake_az.write_text(
                """#!/usr/bin/env bash
set -euo pipefail
case "$*" in
  *"webapp show"*)
    if [ "${WEBAPP_MISSING:-false}" = true ]; then
      printf '%s\\n' "(ResourceNotFound) Web App was not found." >&2
      exit 1
    fi
    printf '%s\\n' "test-api"
    ;;
  *"webapp config show"*) printf '%s\\n' "${LINUX_FX_VERSION}" ;;
  *"webapp sitecontainers show"*)
    if [ "${FAIL_SITECONTAINER_LOOKUP:-false}" = true ]; then exit 1; fi
    printf '%s\\n' "${SITECONTAINER_IMAGE}"
    ;;
  *"webapp config appsettings list"*)
    case "$*" in
      *"ELSA_CONTROL_IMAGE_ID"*) printf '%s\\n' "${IMAGE_ID_OVERRIDE_COUNT:-0}" ;;
      *) printf '%s\\n' "${APPLICATION_BUILD_NUMBER}" ;;
    esac
    ;;
  *"acr manifest show-metadata"*) printf '%s\\n' "${PREVIOUS_DIGEST:-sha256:$(printf 'd%.0s' {1..64})}" ;;
  *) exit 1 ;;
esac
"""
            )
            fake_az.chmod(0o755)
            fake_curl = temp_path / "curl"
            fake_curl.write_text(
                '''#!/usr/bin/env bash
set -euo pipefail
output_file=""
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--output" ]; then
    output_file="$2"
    shift 2
  else
    shift
  fi
done
if [ -n "${HEALTH_RESPONSE:-}" ]; then
  printf '%s' "$HEALTH_RESPONSE" > "$output_file"
else
  printf '%s' '{"status":"ok","buildNumber":"1786839398","imageId":"abcdef0123456789"}' > "$output_file"
fi
printf '%s' "${HEALTH_STATUS:-200}"
'''
            )
            fake_curl.chmod(0o755)

            def run_capture(
                linux_fx_version: str,
                sitecontainer_image: str,
                fail_sitecontainer_lookup: bool = False,
                webapp_missing: bool = False,
                deploy_mode: str = "app",
                health_response: str = '{"status":"ok","buildNumber":"1786839398","imageId":"abcdef0123456789"}',
                health_status: str = "200",
                image_id_override_count: str = "0",
            ) -> subprocess.CompletedProcess[str]:
                output_file = temp_path / "github-output"
                output_file.unlink(missing_ok=True)
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{temp_path}:{environment['PATH']}",
                        "GITHUB_OUTPUT": str(output_file),
                        "LINUX_FX_VERSION": linux_fx_version,
                        "SITECONTAINER_IMAGE": sitecontainer_image,
                        "APPLICATION_BUILD_NUMBER": "1786839398",
                        "FAIL_SITECONTAINER_LOOKUP": str(fail_sitecontainer_lookup).lower(),
                        "WEBAPP_MISSING": str(webapp_missing).lower(),
                        "DEPLOY_MODE": deploy_mode,
                        "AZURE_RESOURCE_GROUP": "test-rg",
                        "AZURE_WEBAPP_NAME": "test-api",
                        "AZURE_CONTAINER_REGISTRY_ENDPOINT": "acr.azurecr.io",
                        "HEALTH_RESPONSE": health_response,
                        "HEALTH_STATUS": health_status,
                        "IMAGE_ID_OVERRIDE_COUNT": image_id_override_count,
                        "PREVIOUS_DIGEST": "sha256:" + "d" * 64,
                    }
                )
                return subprocess.run(
                    ["bash", "-c", capture_script],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )

            unsafe_images = (
                "https://user:pass@acr.azurecr.io/elsa-control/api:latest",
                "acr.azurecr.io/elsa-control/api?secret=1",
                "acr.azurecr.io/elsa-control/api#fragment",
            )
            for image in unsafe_images:
                for runtime, captured_image in (
                    (f"DOCKER|{image}", ""),
                    ("SITECONTAINERS", image),
                ):
                    result = run_capture(runtime, captured_image)
                    self.assertNotEqual(result.returncode, 0, image)
                    if runtime == "SITECONTAINERS":
                        self.assertIn(
                            "unexpected or unsafe format",
                            result.stdout + result.stderr,
                        )

            lookup_failure = run_capture(
                "SITECONTAINERS",
                "acr.azurecr.io/elsa-control/api:latest",
                fail_sitecontainer_lookup=True,
            )
            self.assertNotEqual(lookup_failure.returncode, 0)

            valid_health_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:latest",
                "",
            )
            self.assertEqual(
                valid_health_capture.returncode,
                0,
                valid_health_capture.stderr,
            )
            valid_output = (temp_path / "github-output").read_text()
            self.assertIn("previous_health_build_number=1786839398", valid_output)
            self.assertIn("previous_health_image_id=abcdef0123456789", valid_output)

            unsafe_health_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:latest",
                "",
                health_response='{"status":"ok","buildNumber":"1786839398","imageId":"https://user:pass@example.test/image"}',
            )
            self.assertNotEqual(unsafe_health_capture.returncode, 0)
            self.assertIn(
                "unexpected or unsafe previous_health_image_id",
                unsafe_health_capture.stdout + unsafe_health_capture.stderr,
            )

            unhealthy_current = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:latest",
                "",
                health_response=(
                    '{"status":"degraded","buildNumber":"1786839398",'
                    '"imageId":"https://user:pass@example.test/image"}'
                ),
                health_status="503",
            )
            self.assertEqual(
                unhealthy_current.returncode,
                0,
                unhealthy_current.stderr,
            )
            unhealthy_output = (temp_path / "github-output").read_text()
            self.assertIn("capture_succeeded=true", unhealthy_output)
            self.assertNotIn("previous_health_image_id=", unhealthy_output)

            fresh_infra = run_capture(
                "SITECONTAINERS",
                "",
                webapp_missing=True,
                deploy_mode="infra",
            )
            self.assertEqual(fresh_infra.returncode, 0, fresh_infra.stderr)
            self.assertIn(
                "capture_succeeded=false",
                (temp_path / "github-output").read_text(),
            )

            fresh_app = run_capture(
                "SITECONTAINERS",
                "",
                webapp_missing=True,
                deploy_mode="app",
            )
            self.assertNotEqual(fresh_app.returncode, 0)

            promoted_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:previous",
                "",
                deploy_mode="promote",
            )
            self.assertEqual(0, promoted_capture.returncode, promoted_capture.stderr)
            promoted_output = (temp_path / "github-output").read_text()
            self.assertIn(
                "linux_fx_version=DOCKER|acr.azurecr.io/elsa-control/api@sha256:" + "d" * 64,
                promoted_output,
            )

            override_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:previous",
                "",
                deploy_mode="promote",
                image_id_override_count="1",
            )
            self.assertNotEqual(0, override_capture.returncode)
            self.assertIn("runtime image identity override", override_capture.stdout + override_capture.stderr)

    def test_rollback_identity_query_rejects_a_different_image(self) -> None:
        exact_query = (
            '.status == "ok" and .buildNumber == $expected_build_number '
            'and .imageId == $expected_image_id'
        )
        legacy_query = (
            '.status == "ok" and ((.buildNumber // $expected_build_number) '
            '== $expected_build_number)'
        )

        def evaluate(
            query: str,
            payload: dict[str, str],
            image_id: str = "",
        ) -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                [
                    "jq",
                    "-e",
                    "--arg",
                    "expected_build_number",
                    "1786839398",
                    "--arg",
                    "expected_image_id",
                    image_id,
                    query,
                ],
                input=json.dumps(payload),
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(
            evaluate(
                exact_query,
                {
                    "status": "ok",
                    "buildNumber": "1786839398",
                    "imageId": "abcdef0123456789",
                },
                "abcdef0123456789",
            ).returncode,
            0,
        )
        self.assertNotEqual(
            evaluate(
                exact_query,
                {"status": "ok", "buildNumber": "1786839398", "imageId": "failed-image"},
                "abcdef0123456789",
            ).returncode,
            0,
        )
        self.assertEqual(
            evaluate(
                legacy_query,
                {"status": "ok", "buildNumber": "1786839398"},
            ).returncode,
            0,
        )


if __name__ == "__main__":
    unittest.main()
