using './api-website.module.bicep'

param adminapikey_value = '{{ securedParameter "adminApiKey" }}'
param api_containerimage = '{{ .Image }}'
param api_containerport = '{{ targetPortOrDefault 8080 }}'
param api_identity_outputs_clientid = '{{ .Env.API_IDENTITY_CLIENTID }}'
param api_identity_outputs_id = '{{ .Env.API_IDENTITY_ID }}'
{{ if index .Env "AZURE_PROVISIONER_IDENTITY_ID" }}
param provisioner_identity_outputs_id = '{{ .Env.AZURE_PROVISIONER_IDENTITY_ID }}'
{{ else }}
param provisioner_identity_outputs_id = ''
{{ end }}
{{ if index .Env "AZURE_API_EGRESS_SUBNET_ID" }}
param api_egress_subnet_id = '{{ .Env.AZURE_API_EGRESS_SUBNET_ID }}'
{{ else }}
param api_egress_subnet_id = ''
{{ end }}
param builderclientapikey_value = '{{ securedParameter "builderClientApiKey" }}'
param control_sql_outputs_sqlserverfqdn = '{{ .Env.CONTROL_SQL_SQLSERVERFQDN }}'
param elsa_control_outputs_azure_container_registry_endpoint = '{{ .Env.ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_ENDPOINT }}'
param elsa_control_outputs_azure_container_registry_managed_identity_client_id = '{{ .Env.ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID }}'
param elsa_control_outputs_azure_container_registry_managed_identity_id = '{{ .Env.ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID }}'
param elsa_control_outputs_planid = '{{ .Env.ELSA_CONTROL_PLANID }}'
param entraclientid_value = '{{ parameter "entraClientId" }}'
param entraclientsecret_value = '{{ securedParameter "entraClientSecret" }}'
param entratenantid_value = '{{ parameter "entraTenantId" }}'
