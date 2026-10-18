#include "job_handler.hpp"
#include "log_analysis.hpp"

#include <string>

namespace forge::worker {
namespace {

JobHandlerResult failure(std::string code, std::string message)
{
    return {
        false,
        nlohmann::json::object(),
        std::move(code),
        std::move(message)
    };
}

}

JobHandlerResult handle_job(
    const nlohmann::json& execution_descriptor)
{
    if (!execution_descriptor.is_object() ||
        !execution_descriptor.contains("jobType") ||
        !execution_descriptor["jobType"].is_string())
    {
        return failure(
            "INVALID_EXECUTION_DESCRIPTOR",
            "Execution descriptor must contain a string jobType.");
    }

    const auto job_type =
        execution_descriptor["jobType"].get<std::string>();

    if (job_type != "log-analysis")
    {
        return failure(
            "UNSUPPORTED_JOB_TYPE",
            "The C++ worker does not support this job type.");
    }

    const auto reference = execution_descriptor.value(
        "inputReference", nlohmann::json(nullptr));

    if (!reference.is_null())
    {
        return failure(
            "UNSUPPORTED_ARTIFACT_REFERENCE",
            "Artifact references are not supported by this worker version.");
    }

    if (!execution_descriptor.contains("inputPayload") ||
        !execution_descriptor["inputPayload"].is_object())
    {
        return failure(
            "INVALID_INPUT_SCHEMA",
            "log-analysis requires an inline inputPayload object.");
    }

    const auto& payload = execution_descriptor["inputPayload"];

    if (payload.size() != 1 ||
        !payload.contains("text") ||
        !payload["text"].is_string())
    {
        return failure(
            "INVALID_INPUT_SCHEMA",
            "log-analysis inputPayload must contain only a string text property.");
    }

    const auto text = payload["text"].get<std::string>();

    if (text.find_first_not_of(" \t\r\n") == std::string::npos)
    {
        return failure(
            "INVALID_INPUT_SCHEMA",
            "The text property must contain non-whitespace text.");
    }

    if (text.size() > 64 * 1024)
    {
        return failure(
            "INVALID_INPUT_SCHEMA",
            "The inline text exceeds the 64 KiB limit.");
    }

    return {
        true,
        analyze_log_text(text),
        {},
        {}
    };
}

}
