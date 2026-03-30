"""Fetch temporary AWS credentials via the IoT Credentials Provider endpoint."""

import json
import logging
import os
import ssl
import threading
from datetime import datetime, timezone
from urllib.request import Request, urlopen

import boto3
from botocore.credentials import RefreshableCredentials
from botocore.session import get_session

logger = logging.getLogger("snout-spotter-credentials")

_lock = threading.Lock()


def _fetch_credentials(endpoint: str, role_alias: str, cert_path: str, key_path: str, ca_path: str) -> dict:
    """Call the IoT Credentials Provider HTTPS endpoint using the device certificate."""
    url = f"https://{endpoint}/role-aliases/{role_alias}/credentials"
    ctx = ssl.create_default_context(cafile=ca_path)
    ctx.load_cert_chain(certfile=cert_path, keyfile=key_path)

    req = Request(url, headers={"x-amzn-iot-thingname": os.environ.get("IOT_THING_NAME", "")})
    with urlopen(req, context=ctx, timeout=10) as resp:
        body = json.loads(resp.read())

    creds = body["credentials"]
    return {
        "access_key": creds["accessKeyId"],
        "secret_key": creds["secretAccessKey"],
        "token": creds["sessionToken"],
        "expiry_time": creds["expiration"],
    }


def _derive_credential_endpoint(iot_data_endpoint: str) -> str:
    """Derive the credential provider endpoint from the IoT data endpoint.

    Data:       xxxxx-ats.iot.eu-west-1.amazonaws.com
    Credential: xxxxx.credentials.iot.eu-west-1.amazonaws.com
    """
    return iot_data_endpoint.replace("-ats.iot.", ".credentials.iot.")


def create_session(config: dict) -> boto3.Session:
    """Create a boto3 Session backed by auto-refreshing IoT credentials."""
    iot_cfg = config["iot"]
    cred_cfg = config.get("credentials_provider", {})

    endpoint = cred_cfg.get("endpoint", "") or _derive_credential_endpoint(iot_cfg.get("endpoint", ""))
    role_alias = cred_cfg.get("role_alias", "snoutspotter-pi-role-alias")
    cert_path = os.path.expanduser(iot_cfg["cert_path"])
    key_path = os.path.expanduser(iot_cfg["key_path"])
    ca_path = os.path.expanduser(iot_cfg["root_ca_path"])

    if not endpoint:
        raise ValueError("Cannot determine credentials provider endpoint — set credentials_provider.endpoint or iot.endpoint")

    def refresh():
        with _lock:
            logger.info("Refreshing IoT credentials")
            return _fetch_credentials(endpoint, role_alias, cert_path, key_path, ca_path)

    session_credentials = RefreshableCredentials.create_from_metadata(
        metadata=refresh(),
        refresh_using=refresh,
        method="iot-credentials-provider",
    )

    botocore_session = get_session()
    botocore_session._credentials = session_credentials
    return boto3.Session(botocore_session=botocore_session)
