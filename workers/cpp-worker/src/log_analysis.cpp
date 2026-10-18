#include "log_analysis.hpp"

#include <algorithm>
#include <cctype>
#include <sstream>
#include <string>

namespace forge::worker {

nlohmann::json analyze_log_text(const std::string& text)
{
    std::size_t total_lines = 0;
    std::size_t error_lines = 0;
    std::size_t warn_lines = 0;
    std::size_t info_lines = 0;

    std::istringstream input(text);
    std::string line;

    while (std::getline(input, line))
    {
        ++total_lines;

        std::string uppercase_line = line;
        std::transform(
            uppercase_line.begin(),
            uppercase_line.end(),
            uppercase_line.begin(),
            [](unsigned char ch)
            {
                return static_cast<char>(std::toupper(ch));
            });

        // Count each category at most once per line.
        if (uppercase_line.find("ERROR") != std::string::npos)
        {
            ++error_lines;
        }

        if (uppercase_line.find("WARN") != std::string::npos)
        {
            ++warn_lines;
        }

        if (uppercase_line.find("INFO") != std::string::npos)
        {
            ++info_lines;
        }
    }

    return {
        {"analysisVersion", 1},
        {"totalLines", total_lines},
        {"errorLines", error_lines},
        {"warnLines", warn_lines},
        {"infoLines", info_lines}
    };
}

}
