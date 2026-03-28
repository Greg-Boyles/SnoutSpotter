terraform {
  required_version = ">= 1.5"

  required_providers {
    okta = {
      source  = "okta/okta"
      version = "~> 4.0"
    }
  }
}

provider "okta" {
  org_name  = var.okta_org_name
  base_url  = "okta.com"
  api_token = var.okta_api_token
}

resource "okta_app_oauth" "snoutspotter" {
  label                      = "SnoutSpotter"
  type                       = "browser"
  grant_types                = ["authorization_code"]
  response_types             = ["code"]
  token_endpoint_auth_method = "none"
  pkce_required              = true

  redirect_uris = [
    "https://${var.cloudfront_domain}/login/callback",
    "http://localhost:5173/login/callback",
  ]

  post_logout_redirect_uris = [
    "https://${var.cloudfront_domain}",
    "http://localhost:5173",
  ]

  lifecycle {
    prevent_destroy = true
  }
}


# Access policy on the default authorization server for the SPA app
resource "okta_auth_server_policy" "snoutspotter" {
  auth_server_id   = "default"
  name             = "SnoutSpotter SPA"
  description      = "Access policy for SnoutSpotter frontend"
  priority         = 1
  client_whitelist = [okta_app_oauth.snoutspotter.client_id]
}

resource "okta_auth_server_policy_rule" "snoutspotter" {
  auth_server_id       = "default"
  policy_id            = okta_auth_server_policy.snoutspotter.id
  name                 = "Allow SPA access"
  priority             = 1
  grant_type_whitelist = ["authorization_code"]
  scope_whitelist      = ["openid", "profile", "email"]
  access_token_lifetime_minutes = 60
}
