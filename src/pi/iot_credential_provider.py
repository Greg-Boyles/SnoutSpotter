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


def get_raw_credentials(config: dict) -> dict | None:
    """Fetch raw credentials dict with accessKeyId, secretAccessKey, sessionToken, expiration.

    Returns None if the credential provider endpoint is not configured.
    """
    iot_cfg = config["iot"]
    cred_cfg = config.get("credentials_provider", {})

    endpoint = cred_cfg.get("endpoint", "")
    role_alias = cred_cfg.get("role_alias", "snoutspotter-pi-role-alias")

    if not endpoint:
        return None

    cert_path = os.path.expanduser(iot_cfg["cert_path"])
    key_path = os.path.expanduser(iot_cfg["key_path"])
    ca_path = os.path.expanduser(iot_cfg["root_ca_path"])

    return _fetch_credentials(endpoint, role_alias, cert_path, key_path, ca_path)


def create_session(config: dict) -> boto3.Session:
    """Create a boto3 Session backed by auto-refreshing IoT credentials.

    Falls back to default boto3 credentials (e.g. ~/.aws/credentials) if the
    credential provider endpoint is not configured.
    """
    iot_cfg = config["iot"]
    cred_cfg = config.get("credentials_provider", {})

    endpoint = cred_cfg.get("endpoint", "")
    role_alias = cred_cfg.get("role_alias", "snoutspotter-pi-role-alias")

    if not endpoint:
        logger.warning("credentials_provider.endpoint not configured — using default credentials")
        return boto3.Session()

    cert_path = os.path.expanduser(iot_cfg["cert_path"])
    key_path = os.path.expanduser(iot_cfg["key_path"])
    ca_path = os.path.expanduser(iot_cfg["root_ca_path"])

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
    logger.info("Using IoT Credentials Provider for AWS access")
    return boto3.Session(botocore_session=botocore_session)
