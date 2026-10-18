#include "api_client.hpp"

#include <curl/curl.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <random>
#include <stdexcept>
#include <string>
#include <utility>
#include <thread>

namespace forge::worker {
namespace {

class CurlRuntime {
public:
    CurlRuntime()
    {
        if (curl_global_init(CURL_GLOBAL_DEFAULT) != CURLE_OK)
            throw std::runtime_error("libcurl global initialization failed.");
    }

    ~CurlRuntime() { curl_global_cleanup(); }

    CurlRuntime(const CurlRuntime&) = delete;
    CurlRuntime& operator=(const CurlRuntime&) = delete;
};

CurlRuntime& curl_runtime()
{
    static CurlRuntime runtime;
    return runtime;
}

std::string env_or(
    const char* name,
    const char* fallback)
{
    const char* value = std::getenv(name);
    return value && *value ? value : fallback;
}

std::string trim_trailing_slashes(std::string value)
{
    while (!value.empty() && value.back() == '/')
        value.pop_back();

    return value;
}

std::size_t append_body(
    char* data,
    std::size_t size,
    std::size_t count,
    void* context)
{
    const auto bytes = size * count;
    static_cast<std::string*>(context)->append(data, bytes);
    return bytes;
}

struct CurlHandle {
    CURL* value = curl_easy_init();

    ~CurlHandle()
    {
        if (value)
            curl_easy_cleanup(value);
    }

    CurlHandle(const CurlHandle&) = delete;
    CurlHandle& operator=(const CurlHandle&) = delete;
    CurlHandle() = default;
};

struct HeaderList {
    curl_slist* value = nullptr;

    void add(const std::string& header)
    {
        auto* next = curl_slist_append(value, header.c_str());
        if (!next)
            throw std::runtime_error("Could not allocate HTTP headers.");
        value = next;
    }

    ~HeaderList()
    {
        if (value)
            curl_slist_free_all(value);
    }
};

std::string url_encode(CURL* curl, const std::string& value)
{
    char* encoded = curl_easy_escape(
        curl, value.c_str(), static_cast<int>(value.size()));

    if (!encoded)
        throw std::runtime_error("Could not URL-encode form value.");

    std::string result(encoded);
    curl_free(encoded);
    return result;
}

HttpResponse perform_request(
    const std::string& url,
    const std::string& body,
    const std::string& bearer_token,
    const std::string& content_type)
{
    CurlHandle handle;
    if (!handle.value)
        throw std::runtime_error("Could not initialize libcurl.");

    std::string response_body;
    char error_buffer[CURL_ERROR_SIZE]{};

    HeaderList headers;
    headers.add("Content-Type: " + content_type);
    headers.add("Accept: application/json");

    if (!bearer_token.empty())
        headers.add("Authorization: Bearer " + bearer_token);

    curl_easy_setopt(handle.value, CURLOPT_URL, url.c_str());
    curl_easy_setopt(handle.value, CURLOPT_POST, 1L);
    curl_easy_setopt(handle.value, CURLOPT_POSTFIELDS, body.data());
    curl_easy_setopt(
        handle.value,
        CURLOPT_POSTFIELDSIZE_LARGE,
        static_cast<curl_off_t>(body.size()));
    curl_easy_setopt(handle.value, CURLOPT_HTTPHEADER, headers.value);
    curl_easy_setopt(handle.value, CURLOPT_WRITEFUNCTION, append_body);
    curl_easy_setopt(handle.value, CURLOPT_WRITEDATA, &response_body);
    curl_easy_setopt(handle.value, CURLOPT_ERRORBUFFER, error_buffer);
    curl_easy_setopt(handle.value, CURLOPT_CONNECTTIMEOUT, 5L);
    curl_easy_setopt(handle.value, CURLOPT_TIMEOUT, 15L);
    curl_easy_setopt(handle.value, CURLOPT_NOSIGNAL, 1L);

    const auto result = curl_easy_perform(handle.value);

    HttpResponse response;
    response.body = std::move(response_body);

    if (result != CURLE_OK) {
        response.error = error_buffer[0]
            ? error_buffer
            : curl_easy_strerror(result);
        return response;
    }

    curl_easy_getinfo(
        handle.value, CURLINFO_RESPONSE_CODE, &response.status);
    response.transport_ok = true;
    return response;
}

}

ApiClient::ApiClient()
    : api_base_url_(trim_trailing_slashes(
          env_or("FORGE_API_URL", "http://127.0.0.1:5285"))),
      keycloak_base_url_(trim_trailing_slashes(
          env_or("FORGE_KEYCLOAK_URL", "http://localhost:8080"))),
      realm_(env_or("FORGE_KEYCLOAK_REALM", "forge")),
      client_id_(env_or("FORGE_KEYCLOAK_CLIENT_ID", "forge-local-test")),
      client_secret_(env_or("KEYCLOAK_CLIENT_SECRET", ""))
{
    (void)curl_runtime();

    if (client_secret_.empty())
        throw std::runtime_error(
            "KEYCLOAK_CLIENT_SECRET environment variable is required.");
}

std::string ApiClient::acquire_token()
{
    std::lock_guard lock(token_mutex_);

    const auto now = std::chrono::steady_clock::now();
    if (!cached_token_.empty() && now < token_expiry_)
        return cached_token_;

    CurlHandle handle;
    if (!handle.value)
        throw std::runtime_error("Could not initialize libcurl.");

    const auto form =
        "grant_type=client_credentials&client_id=" +
        url_encode(handle.value, client_id_) +
        "&client_secret=" +
        url_encode(handle.value, client_secret_);

    const auto response = perform_request(
        keycloak_base_url_ + "/realms/" + realm_ +
            "/protocol/openid-connect/token",
        form,
        "",
        "application/x-www-form-urlencoded");

    if (!response.transport_ok)
        throw std::runtime_error(
            "Keycloak token request failed: " + response.error);

    if (response.status != 200)
        throw std::runtime_error(
            "Keycloak rejected the token request with HTTP " +
            std::to_string(response.status));

    const auto json = nlohmann::json::parse(response.body);
    if (!json.contains("access_token") ||
        !json["access_token"].is_string())
        throw std::runtime_error(
            "Keycloak response did not contain an access token.");

    cached_token_ = json["access_token"].get<std::string>();

    const auto expires_in = json.value("expires_in", 60);
    const auto cache_seconds = std::max(expires_in - 30, 1);

    token_expiry_ = now + std::chrono::seconds(cache_seconds);
    return cached_token_;
}

HttpResponse ApiClient::perform_post(
    const std::string& url,
    const std::string& body,
    const std::string& bearer_token)
{
    return perform_request(
        url, body, bearer_token, "application/json");
}

HttpResponse ApiClient::post_json(
    const std::string& path,
    const nlohmann::json& payload)
{
    // Serialize once so all retries use the same body and request IDs.
    const auto body = payload.dump();
    constexpr int max_attempts = 3;

    for (int attempt = 0; attempt < max_attempts; ++attempt) {
        HttpResponse response;

        try {
            // Allow one token refresh without changing the operation payload.
            for (int auth_attempt = 0; auth_attempt < 2; ++auth_attempt) {
                const auto token = acquire_token();
                response = perform_post(
                    api_base_url_ + path,
                    body,
                    token);

                if (!response.transport_ok || response.status != 401)
                    break;

                std::lock_guard lock(token_mutex_);
                cached_token_.clear();
                token_expiry_ = {};
            }
        } catch (const std::exception& exception) {
            response = HttpResponse{};
            response.error = exception.what();
        }

        // A second 401 indicates authentication still failed.
        if (response.transport_ok && response.status == 401)
            return response;

        const bool retryable =
            !response.transport_ok ||
            response.status == 408 ||
            response.status == 429 ||
            response.status >= 500;

        if (!retryable || attempt + 1 == max_attempts)
            return response;

        // Bounded backoff: 200 ms, then 400 ms.
        std::this_thread::sleep_for(
            std::chrono::milliseconds(200 * (1 << attempt)));
    }

    HttpResponse response;
    response.error = "HTTP request attempts exhausted.";
    return response;
}

std::string ApiClient::new_guid()
{
    std::array<std::uint8_t, 16> bytes{};
    std::random_device random;

    for (auto& byte : bytes)
        byte = static_cast<std::uint8_t>(random());

    bytes[6] = static_cast<std::uint8_t>((bytes[6] & 0x0F) | 0x40);
    bytes[8] = static_cast<std::uint8_t>((bytes[8] & 0x3F) | 0x80);

    char result[37]{};
    std::snprintf(
        result, sizeof(result),
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-"
        "%02x%02x%02x%02x%02x%02x",
        static_cast<unsigned int>(bytes[0]),
        static_cast<unsigned int>(bytes[1]),
        static_cast<unsigned int>(bytes[2]),
        static_cast<unsigned int>(bytes[3]),
        static_cast<unsigned int>(bytes[4]),
        static_cast<unsigned int>(bytes[5]),
        static_cast<unsigned int>(bytes[6]),
        static_cast<unsigned int>(bytes[7]),
        static_cast<unsigned int>(bytes[8]),
        static_cast<unsigned int>(bytes[9]),
        static_cast<unsigned int>(bytes[10]),
        static_cast<unsigned int>(bytes[11]),
        static_cast<unsigned int>(bytes[12]),
        static_cast<unsigned int>(bytes[13]),
        static_cast<unsigned int>(bytes[14]),
        static_cast<unsigned int>(bytes[15]));

    return result;
}

HttpResponse ApiClient::claim_job(
    const std::string& job_id,
    const std::string& claim_request_id,
    const std::string& worker_instance_id)
{
    return post_json("/api/v1/jobs/claim", {
        {"jobId", job_id},
        {"claimRequestId", claim_request_id},
        {"workerInstanceId", worker_instance_id}
    });
}

HttpResponse ApiClient::renew_lease(
    const std::string& job_id,
    const std::string& attempt_id,
    const std::string& worker_instance_id,
    long long fencing_token,
    const std::string& renew_request_id)
{
    return post_json("/api/v1/jobs/renew-lease", {
        {"renewRequestId", renew_request_id},
        {"jobId", job_id},
        {"attemptId", attempt_id},
        {"workerInstanceId", worker_instance_id},
        {"fencingToken", fencing_token}
    });
}

HttpResponse ApiClient::complete_succeeded(
    const std::string& job_id,
    const std::string& attempt_id,
    const std::string& worker_instance_id,
    long long fencing_token,
    const std::string& completion_request_id,
    const nlohmann::json& result_descriptor)
{
    if (!result_descriptor.is_object())
        throw std::invalid_argument(
            "result_descriptor must be a JSON object.");

    return post_json("/api/v1/jobs/complete", {
        {"completionRequestId", completion_request_id},
        {"jobId", job_id},
        {"attemptId", attempt_id},
        {"workerInstanceId", worker_instance_id},
        {"fencingToken", fencing_token},
        {"outcome", "Succeeded"},
        {"resultDescriptor", result_descriptor}
    });
}

HttpResponse ApiClient::complete_failed(
    const std::string& job_id,
    const std::string& attempt_id,
    const std::string& worker_instance_id,
    long long fencing_token,
    const std::string& completion_request_id,
    const std::string& failure_code,
    const std::string& failure_message)
{
    return post_json("/api/v1/jobs/complete", {
        {"completionRequestId", completion_request_id},
        {"jobId", job_id},
        {"attemptId", attempt_id},
        {"workerInstanceId", worker_instance_id},
        {"fencingToken", fencing_token},
        {"outcome", "Failed"},
        {"failureCode", failure_code},
        {"failureMessage", failure_message}
    });
}

HttpResponse ApiClient::complete_cancelled(
    const std::string& job_id,
    const std::string& attempt_id,
    const std::string& worker_instance_id,
    long long fencing_token,
    const std::string& completion_request_id,
    const std::string& cancellation_details)
{
    nlohmann::json payload = {
        {"completionRequestId", completion_request_id},
        {"jobId", job_id},
        {"attemptId", attempt_id},
        {"workerInstanceId", worker_instance_id},
        {"fencingToken", fencing_token},
        {"outcome", "Cancelled"}
    };

    if (!cancellation_details.empty())
        payload["cancellationDetails"] = cancellation_details;

    return post_json("/api/v1/jobs/complete", payload);
}

}
