output "okta_client_id" {
  description = "Okta SPA application client ID"
  value       = okta_app_oauth.snoutspotter.client_id
}

output "okta_issuer" {
  description = "Okta authorization server issuer URL"
  value       = "https://${var.okta_org_name}.okta.com/oauth2/default"
}
