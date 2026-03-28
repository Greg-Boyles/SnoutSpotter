terraform {
  required_version = ">= 1.5"

  required_providers {
    okta = {
      source  = "okta/okta"
      version = "~> 4.0"
    }
  }

  backend "s3" {
    bucket  = "snout-spotter-490204853569"
    key     = "terraform/okta/terraform.tfstate"
    region  = "eu-west-1"
    encrypt = true
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
  authentication_policy      = okta_app_signon_policy.snoutspotter.id

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

# Sign-on policy — password only, no MFA required
resource "okta_app_signon_policy" "snoutspotter" {
  name        = "SnoutSpotter Sign-On Policy"
  description = "Password only, no MFA required"
}

resource "okta_app_signon_policy_rule" "no_mfa" {
  policy_id           = okta_app_signon_policy.snoutspotter.id
  name                = "Allow password only"
  factor_mode         = "NO_FACTOR"
  re_authentication_frequency = "PT0S"
  groups_included     = [okta_group.snoutspotter_users.id]
  depends_on          = [okta_group.snoutspotter_users]
}

resource "okta_group" "snoutspotter_users" {
  name        = "SnoutSpotter Users"
  description = "Users with access to the SnoutSpotter application"
}

resource "okta_app_group_assignment" "snoutspotter_users" {
  app_id   = okta_app_oauth.snoutspotter.id
  group_id = okta_group.snoutspotter_users.id
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
  auth_server_id        = "default"
  policy_id             = okta_auth_server_policy.snoutspotter.id
  name                  = "Allow SPA access"
  priority              = 1
  grant_type_whitelist  = ["authorization_code"]
  scope_whitelist       = ["openid", "profile", "email"]
  group_whitelist       = [okta_group.snoutspotter_users.id]
  access_token_lifetime_minutes = 60
}
